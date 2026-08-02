using Domain;
using Echo.Realtime;
using Messaging.Contracts.Bus.Events;
using Facet.Extensions;
using Messaging.Application.Dtos.Request;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Mls;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Services;

public enum MlsOperationStatus
{
    Ok,
    NotFound,
    Conflict,
    BadRequest,
}

/// <summary>Transport-agnostic outcome so the service can be unit-tested on domain behaviour rather
/// than on which <c>IResult</c> subtype came back.</summary>
public record MlsOperationResult(MlsOperationStatus Status, object? Value = null, string? Error = null)
{
    public static MlsOperationResult Ok(object? value = null) => new(MlsOperationStatus.Ok, value);
    public static MlsOperationResult NotFound(string error) => new(MlsOperationStatus.NotFound, null, error);
    public static MlsOperationResult Conflict(object value) => new(MlsOperationStatus.Conflict, value);
    public static MlsOperationResult BadRequest(string error) => new(MlsOperationStatus.BadRequest, null, error);
}

/// <summary>
/// The MLS group lifecycle for one context, shared by conversations and guild channels.
/// </summary>
public class MlsGroupService(
    MicroserviceContext ctx,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus,
    MlsJoinRequestService joinRequests)
{
    /// <summary>Minimum spacing between toggles of the same context.</summary>
    public static readonly TimeSpan ToggleCooldown = TimeSpan.FromSeconds(30);

    /// <summary>How long a commit stays fetchable.</summary>
    public static readonly TimeSpan CommitRetention = TimeSpan.FromDays(30);

    /// <summary>Cap on one catch-up page.</summary>
    public const int MaxCommitPageSize = 200;

    /// <summary>Ceiling on how many Welcomes one call may park.</summary>
    public const int MaxWelcomesPerCall = 1000;

    public Task<MlsGroupGeneration?> GetActiveGenerationAsync(string contextId) =>
        ctx.MlsGroupGenerations
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

    /// <summary>
    /// Whether the server has any evidence this user has ever been inside this MLS group.
    /// </summary>
    public async Task<bool> HasGroupParticipationAsync(string contextId, int generation, string userId)
    {
        var activated = await ctx.MlsGroupGenerations.AsNoTracking()
            .AnyAsync(g => g.ContextId == contextId
                           && g.Generation == generation
                           && g.ActivatedByUserId == userId);
        if (activated) return true;

        var welcomed = await ctx.PendingWelcomes.AsNoTracking()
            .AnyAsync(w => w.ContextId == contextId && w.Generation == generation && w.UserId == userId);
        if (welcomed) return true;

        return await ctx.MlsCommits.AsNoTracking()
            .AnyAsync(c => c.ContextId == contextId && c.Generation == generation && c.SenderUserId == userId);
    }

    /// <summary>A context's encryption state.</summary>
    public async Task<MlsContextStateDto> GetStateAsync(string contextId, string? callerUserId = null)
    {
        var generations = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .Where(g => g.ContextId == contextId)
            .OrderBy(g => g.Generation)
            .ToListAsync();

        var active = generations.FirstOrDefault(g => g.State == MlsGenerationState.Active);

        var mayHaveGroupInfo = active is not null
                               && (MlsPolicy.ServeGroupInfoToNonParticipants
                                   || (callerUserId is not null
                                       && await HasGroupParticipationAsync(
                                           contextId, active.Generation, callerUserId)));

        return new MlsContextStateDto
        {
            ContextId = contextId,
            Encrypted = active is not null,
            ActiveGeneration = active?.Generation,
            Epoch = active?.Epoch,
            MlsGroupId = active?.MlsGroupId,
            MlsGroupInfo = mayHaveGroupInfo ? active?.MlsGroupInfo : null,
            GroupInfoWithheld = active?.MlsGroupInfo is { Length: > 0 } && !mayHaveGroupInfo,
            // Past generations are listed because their messages are still in the channel.
            Generations = generations.SelectFacets<MlsGroupGeneration, MlsGenerationDto>().ToList(),
        };
    }

    /// <summary>Turns encryption on by minting the next generation.</summary>
    public async Task<MlsOperationResult> EnableAsync(
        string contextId,
        string? conversationId,
        string? channelId,
        string userId,
        EnableMlsDto dto,
        DateTimeOffset now)
    {
        if (dto.MlsGroupId is null || dto.MlsGroupId.Length == 0)
            return MlsOperationResult.BadRequest("MlsGroupId is required");
        if (dto.Epoch < 0)
            return MlsOperationResult.BadRequest("Epoch must be non-negative");
        if (dto.Welcomes.Count > MaxWelcomesPerCall)
            return MlsOperationResult.BadRequest($"At most {MaxWelcomesPerCall} Welcomes per call");

        var generations = await ctx.MlsGroupGenerations
            .Where(g => g.ContextId == contextId)
            .ToListAsync();

        if (generations.Any(g => g.State == MlsGenerationState.Active))
        {
            return MlsOperationResult.Conflict(new MlsToggleConflictDto
            {
                ContextId = contextId,
                Encrypted = true,
                Reason = "Encryption is already enabled for this context.",
            });
        }

        var cooldownError = CheckCooldown(generations, now);
        if (cooldownError is not null) return cooldownError;

        // Conversations carry their MLS state on the aggregate as well.
        Conversation? conversation = null;
        if (conversationId is not null)
        {
            conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null) return MlsOperationResult.NotFound("Conversation not found");
        }

        var generation = MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = contextId,
            ConversationId = conversationId,
            ChannelId = channelId,
            Generation = generations.Count == 0 ? 1 : generations.Max(g => g.Generation) + 1,
            MlsGroupId = dto.MlsGroupId,
            MlsGroupInfo = dto.MlsGroupInfo,
            Epoch = dto.Epoch,
            ActivatedByUserId = userId,
            ActivatedAt = now,
        });
        ctx.MlsGroupGenerations.Add(generation);

        var recipients = await ResolveWelcomeRecipientsAsync(contextId, conversationId, bootstrapping: true);
        var stored = StoreWelcomes(
            dto.Welcomes, contextId, conversationId, channelId, generation.Generation, dto.Epoch, recipients);

        if (conversation is not null)
        {
            conversation.EncryptionState = ChannelEncryptionState.Encrypted;
            conversation.MlsGroupId = dto.MlsGroupId;
            conversation.MlsEpoch = dto.Epoch;
            conversation.MlsGroupInfo = dto.MlsGroupInfo;
        }

        await ctx.SaveChangesAsync();

        await NotifyStateChanged(contextId, channelId, conversationId, encrypted: true, generation.Generation, userId);
        await PushWelcomes(stored, contextId);

        return MlsOperationResult.Ok(new MlsToggleResultDto
        {
            ContextId = contextId,
            Encrypted = true,
            Generation = generation.Generation,
        });
    }

    /// <summary>Turns encryption off by terminating the active generation.</summary>
    public async Task<MlsOperationResult> DisableAsync(
        string contextId,
        string? conversationId,
        string? channelId,
        string userId,
        DateTimeOffset now)
    {
        var generations = await ctx.MlsGroupGenerations
            .Where(g => g.ContextId == contextId)
            .ToListAsync();

        var active = generations.FirstOrDefault(g => g.State == MlsGenerationState.Active);
        if (active is null)
        {
            // Idempotent rather than a conflict: two admins hitting the switch, or one client
            // retrying a request whose response it never saw, should converge on "it is off" and
            // not on an error the user has to interpret.
            return MlsOperationResult.Ok(new MlsToggleResultDto
            {
                ContextId = contextId,
                Encrypted = false,
                Generation = generations.Count == 0 ? null : generations.Max(g => g.Generation),
                AlreadyInRequestedState = true,
            });
        }

        var cooldownError = CheckCooldown(generations, now);
        if (cooldownError is not null) return cooldownError;

        // Same reason as EnableAsync: resolved before the first mutation, because a NotFound taken
        // after Terminate() would still be committed by the transactional middleware and leave the
        // context with no active generation and no way back to one.
        Conversation? conversation = null;
        if (conversationId is not null)
        {
            conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null) return MlsOperationResult.NotFound("Conversation not found");
        }

        active.Terminate(userId, now);

        if (conversation is not null)
        {
            conversation.EncryptionState = ChannelEncryptionState.Plain;
            // MlsGroupId/Epoch/GroupInfo are deliberately left in place: they describe the group the
            // already-sent ciphertext belongs to, and clearing them would strand those messages with
            // nothing pointing at the group that can read them.
        }

        // Welcomes for the terminated generation are worthless - the group is gone.
        var orphaned = await ctx.PendingWelcomes
            .Where(w => w.ContextId == contextId && w.Generation == active.Generation && w.ConsumedAt == null)
            .ToListAsync();
        foreach (var welcome in orphaned) welcome.ConsumedAt = now;

        await ctx.SaveChangesAsync();

        await NotifyStateChanged(contextId, channelId, conversationId, encrypted: false, active.Generation, userId);

        return MlsOperationResult.Ok(new MlsToggleResultDto
        {
            ContextId = contextId,
            Encrypted = false,
            Generation = active.Generation,
            TerminatedGeneration = active.Generation,
        });
    }

    public async Task<MlsOperationResult> PublishCommitAsync(
        string contextId,
        string? conversationId,
        string? channelId,
        string userId,
        PublishMlsCommitDto dto,
        IReadOnlyCollection<string> notifyUserIds,
        DateTimeOffset now)
    {
        if (dto.Commit is null || dto.Commit.Length == 0)
            return MlsOperationResult.BadRequest("Commit is required");
        if (string.IsNullOrWhiteSpace(dto.SenderDeviceId))
            return MlsOperationResult.BadRequest("SenderDeviceId is required");
        if (dto.Welcomes.Count > MaxWelcomesPerCall)
            return MlsOperationResult.BadRequest($"At most {MaxWelcomesPerCall} Welcomes per call");

        if (SizeError(dto) is { } sizeError) return MlsOperationResult.BadRequest(sizeError);

        var active = await GetActiveGenerationAsync(contextId);
        if (active is null)
        {
            return MlsOperationResult.Conflict(new MlsToggleConflictDto
            {
                ContextId = contextId,
                Encrypted = false,
                Reason = "Encryption is not enabled for this context.",
            });
        }

        // Publishing a commit advances the group's epoch for everybody, and on a channel this route
        // needed only ViewChannel - so somebody who had never been in the group could move the
        // epoch to a value no member could reach.
        if (!await HasGroupParticipationAsync(contextId, active.Generation, userId))
        {
            return MlsOperationResult.BadRequest(
                "You are not in this MLS group. Being able to see the context is not being in its "
                + "group - request admission first.");
        }

        // The declared kind, checked against the payload.
        if (InspectCommit(dto, active) is { } shapeError) return MlsOperationResult.BadRequest(shapeError);

        // A commit built against a group that has since been replaced must not be applied to the
        // current one - it would advance the wrong group and fork everybody.
        if (dto.Generation is { } requested && requested != active.Generation)
        {
            return MlsOperationResult.Conflict(new MlsEpochConflictDto
            {
                CurrentEpoch = active.Epoch,
                RejectedEpoch = dto.Epoch,
                CurrentGeneration = active.Generation,
                RejectedGeneration = requested,
                Reason = $"This commit targets generation {requested}; the context is on generation {active.Generation}.",
            });
        }

        // Re-publishing something already stored is a success, not a conflict.
        var replay = await ctx.MlsCommits.AsNoTracking().FirstOrDefaultAsync(c =>
            c.ContextId == contextId
            && c.Generation == active.Generation
            && c.Epoch == dto.Epoch
            // Sender user as well as sender device: ClientDeviceId is client-chosen and unique only
            // per account, so two members of one channel can legitimately share one.
            && c.SenderUserId == userId
            && c.SenderDeviceId == dto.SenderDeviceId
            && c.IsProposal == dto.IsProposal);

        if (replay is not null && replay.Commit.AsSpan().SequenceEqual(dto.Commit))
        {
            return MlsOperationResult.Ok(new MlsCommitPublishedDto
            {
                ContextId = contextId,
                ConversationId = conversationId,
                Generation = replay.Generation,
                Epoch = replay.Epoch,
                IsProposal = replay.IsProposal,
                Duplicate = true,
            });
        }

        // A proposal is not a commit.
        if (dto.IsProposal)
        {
            if (dto.Epoch < active.Epoch)
            {
                return MlsOperationResult.Conflict(new MlsEpochConflictDto
                {
                    CurrentEpoch = active.Epoch,
                    RejectedEpoch = dto.Epoch,
                    CurrentGeneration = active.Generation,
                    RejectedGeneration = active.Generation,
                    Reason = $"Proposals must target epoch {active.Epoch} or later; catch up and re-issue.",
                });
            }
        }
        else if (dto.Epoch != active.Epoch + 1)
        {
            return MlsOperationResult.Conflict(new MlsEpochConflictDto
            {
                CurrentEpoch = active.Epoch,
                RejectedEpoch = dto.Epoch,
                CurrentGeneration = active.Generation,
                RejectedGeneration = dto.Generation ?? active.Generation,
                Reason = $"Expected epoch {active.Epoch + 1}; catch up and re-issue the change.",
            });
        }

        // Belt to the unique index's braces.
        if (!dto.IsProposal)
        {
            var epochTaken = await ctx.MlsCommits.AnyAsync(c =>
                c.ContextId == contextId && c.Generation == active.Generation && c.Epoch == dto.Epoch
                && !c.IsProposal);
            if (epochTaken)
            {
                return MlsOperationResult.Conflict(EpochRaceLost(active, dto.Epoch));
            }
        }

        ctx.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = contextId,
            ConversationId = conversationId,
            ChannelId = channelId,
            Generation = active.Generation,
            Epoch = dto.Epoch,
            Commit = dto.Commit,
            SenderUserId = userId,
            SenderDeviceId = dto.SenderDeviceId,
            IsProposal = dto.IsProposal,
        }));

        if (!dto.IsProposal)
        {
            active.Epoch = dto.Epoch;
            active.UpdatedAt = now;
            if (dto.GroupInfo is { Length: > 0 }) active.MlsGroupInfo = dto.GroupInfo;
        }

        if (conversationId is not null && !dto.IsProposal)
        {
            var conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is not null)
            {
                conversation.MlsEpoch = dto.Epoch;
                if (dto.GroupInfo is { Length: > 0 }) conversation.MlsGroupInfo = dto.GroupInfo;
            }
        }

        var recipients = await ResolveWelcomeRecipientsAsync(contextId, conversationId, bootstrapping: false);
        var storedWelcomes = StoreWelcomes(
            dto.Welcomes, contextId, conversationId, channelId, active.Generation, dto.Epoch, recipients);

        // In the same transaction as the commit: a request marked fulfilled by a commit that then
        // failed to store would look admitted while holding no leaf at all.
        var kind = conversationId is not null ? MlsContextKind.Conversation : MlsContextKind.Channel;
        var required = await joinRequests.RequiredApprovalsFor(contextId, active.Generation, kind);

        var admitted = await joinRequests.FulfilAsync(
            contextId,
            dto.FulfilledJoinRequestIds,
            storedWelcomes.Select(w => (w.UserId, w.DeviceId)).ToList(),
            required,
            now);

        await PruneExpiredCommitsAsync(contextId, now);

        // Save before the fanout, not after.
        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsEpochUniqueViolation(ex))
        {
            // The read-then-write above is inherently racy; the database is what actually decides.
            ctx.ChangeTracker.Clear();
            var current = await ctx.MlsGroupGenerations.AsNoTracking()
                .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

            return MlsOperationResult.Conflict(EpochRaceLost(current ?? active, dto.Epoch));
        }

        await NotifyCommit(contextId, conversationId, channelId, active.Generation, dto, notifyUserIds);

        await AnnounceAdmissionsAsync(admitted, contextId, conversationId, channelId, active.Generation, notifyUserIds);

        await PushWelcomes(storedWelcomes, contextId);

        return MlsOperationResult.Ok(new MlsCommitPublishedDto
        {
            ContextId = contextId,
            ConversationId = conversationId,
            Generation = active.Generation,
            Epoch = dto.Epoch,
            IsProposal = dto.IsProposal,
        });
    }

    /// <summary>
    /// Every commit above <paramref name="sinceEpoch"/> in one generation, in epoch order - the only
    /// path by which a client should advance its group state.
    /// </summary>
    public async Task<List<MlsCommitResponseDto>> GetCommitsAsync(string contextId, int? generation, long sinceEpoch)
    {
        var resolved = generation;
        if (resolved is null)
        {
            // No generation named means "the one that is live now".
            var active = await ctx.MlsGroupGenerations
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

            // Nothing is live: fall back to the most recent generation rather than to "all of
            // them".
            resolved = active?.Generation ?? await ctx.MlsGroupGenerations
                .AsNoTracking()
                .Where(g => g.ContextId == contextId)
                .OrderByDescending(g => g.Generation)
                .Select(g => (int?)g.Generation)
                .FirstOrDefaultAsync();

            if (resolved is null) return [];
        }

        var commits = await ctx.MlsCommits.AsNoTracking()
            .Where(c => c.ContextId == contextId && c.Generation == resolved && c.Epoch > sinceEpoch)
            .OrderBy(c => c.Epoch)
            // A proposal and the commit that follows it can share an epoch, since a proposal does not
            // claim one. Publication order is the order they must be applied in.
            .ThenBy(c => c.CreatedAt)
            .Take(MaxCommitPageSize)
            .ToListAsync();

        return commits.SelectFacets<MlsCommit, MlsCommitResponseDto>().ToList();
    }

    /// <summary>Per-artifact size ceilings.</summary>
    public const int MaxCommitBytes = 1024 * 1024;
    public const int MaxGroupInfoBytes = 1024 * 1024;
    public const int MaxWelcomeBytes = 256 * 1024;
    public const int MaxKeyPackageBytes = 64 * 1024;

    private static string? SizeError(PublishMlsCommitDto dto)
    {
        if (dto.Commit.LongLength > MaxCommitBytes)
            return $"commit exceeds {MaxCommitBytes} bytes";

        if (dto.GroupInfo is { } info && info.LongLength > MaxGroupInfoBytes)
            return $"groupInfo exceeds {MaxGroupInfoBytes} bytes";

        foreach (var welcome in dto.Welcomes)
        {
            if (welcome.Welcome is { } bytes && bytes.LongLength > MaxWelcomeBytes)
                return $"a welcome exceeds {MaxWelcomeBytes} bytes";
        }

        return null;
    }

    /// <summary>Checks the payload against what the caller says it is.</summary>
    private static string? InspectCommit(PublishMlsCommitDto dto, MlsGroupGeneration active)
    {
        if (!MlsMessageInspector.TryRead(dto.Commit, out var header, out var error))
            return error;

        if (header.Version != MlsMessageInspector.Mls10)
            return $"unsupported MLS protocol version {header.Version}";

        if (header.WireFormat is not (MlsWireFormat.PublicMessage or MlsWireFormat.PrivateMessage))
            return $"a commit must be a PublicMessage or PrivateMessage, not {header.WireFormat}";

        var declaredProposal = header.ContentType == MlsContentType.Proposal;

        if (header.ContentType is not (MlsContentType.Proposal or MlsContentType.Commit))
            return $"an application message cannot be published as a commit (content type {header.ContentType})";

        if (declaredProposal != dto.IsProposal)
        {
            return dto.IsProposal
                ? "isProposal is true but the payload is a commit"
                : "isProposal is false but the payload is a proposal; a proposal does not advance the epoch";
        }

        if (active.MlsGroupId is { Length: > 0 } expected
            && header.GroupId is { } actual
            && !expected.AsSpan().SequenceEqual(actual))
        {
            return "this payload belongs to a different MLS group";
        }

        if (dto.GroupInfo is { Length: > 0 } groupInfo)
        {
            if (!MlsMessageInspector.TryRead(groupInfo, out var infoHeader, out var infoError))
                return $"groupInfo: {infoError}";

            if (infoHeader.WireFormat != MlsWireFormat.GroupInfo)
                return $"groupInfo must be an MLSMessage of wire format GroupInfo, not {infoHeader.WireFormat}";
        }

        return null;
    }

    private static MlsEpochConflictDto EpochRaceLost(MlsGroupGeneration generation, long rejectedEpoch) => new()
    {
        CurrentEpoch = generation.Epoch,
        RejectedEpoch = rejectedEpoch,
        CurrentGeneration = generation.Generation,
        RejectedGeneration = generation.Generation,
        Reason = "Another member already committed this epoch.",
    };

    /// <summary>
    /// Postgres reports a unique-index violation as SQLSTATE 23505. Matched on the code rather than
    /// on the index name so a rename does not silently turn this back into a 500, and narrowly
    /// enough that a genuinely different constraint failure still surfaces as one.
    /// </summary>
    private static bool IsEpochUniqueViolation(DbUpdateException ex)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            var sqlState = inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
            if (sqlState == "23505") return true;
        }

        return false;
    }

    /// <summary>Drops commits past the retention window, except any a device still needs.</summary>
    private async Task PruneExpiredCommitsAsync(string contextId, DateTimeOffset now)
    {
        var cutoff = now - CommitRetention;

        var candidates = await ctx.MlsCommits
            .Where(c => c.ContextId == contextId && c.CreatedAt < cutoff)
            .ToListAsync();

        if (candidates.Count == 0) return;

        var floors = (await ctx.PendingWelcomes
                .AsNoTracking()
                .Where(w => w.ContextId == contextId && w.ConsumedAt == null)
                .Select(w => new { w.Generation, w.Epoch })
                .ToListAsync())
            .GroupBy(w => w.Generation)
            .ToDictionary(g => g.Key, g => g.Min(w => w.Epoch));

        var expired = candidates
            .Where(c => !floors.TryGetValue(c.Generation, out var floor) || c.Epoch <= floor)
            .ToList();

        if (expired.Count > 0) ctx.MlsCommits.RemoveRange(expired);
    }

    private static MlsOperationResult? CheckCooldown(List<MlsGroupGeneration> generations, DateTimeOffset now)
    {
        if (generations.Count == 0) return null;

        var lastChange = generations
            .Select(g => g.TerminatedAt ?? g.ActivatedAt)
            .Max();

        var elapsed = now - lastChange;
        if (elapsed >= ToggleCooldown) return null;

        var retryAfter = ToggleCooldown - elapsed;
        return new MlsOperationResult(MlsOperationStatus.Conflict, new MlsToggleConflictDto
        {
            ContextId = generations[0].ContextId,
            Encrypted = generations.Any(g => g.State == MlsGenerationState.Active),
            RetryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds),
            Reason = "Encryption was toggled too recently; let clients settle before changing it again.",
        });
    }

    /// <summary>Who a Welcome may be addressed to.</summary>
    private async Task<HashSet<string>?> ResolveWelcomeRecipientsAsync(
        string contextId, string? conversationId, bool bootstrapping)
    {
        if (conversationId is not null)
        {
            return (await ctx.Members
                    .AsNoTracking()
                    .Where(m => m.ConversationId == conversationId)
                    .Select(m => m.UserId)
                    .ToListAsync())
                .ToHashSet();
        }

        if (bootstrapping) return null;

        var participants = await ctx.PendingWelcomes.AsNoTracking()
            .Where(w => w.ContextId == contextId)
            .Select(w => w.UserId)
            .Distinct()
            .ToListAsync();

        var committers = await ctx.MlsCommits.AsNoTracking()
            .Where(c => c.ContextId == contextId)
            .Select(c => c.SenderUserId)
            .Distinct()
            .ToListAsync();

        var activators = await ctx.MlsGroupGenerations.AsNoTracking()
            .Where(g => g.ContextId == contextId)
            .Select(g => g.ActivatedByUserId)
            .Distinct()
            .ToListAsync();

        var requesters = await ctx.MlsJoinRequests.AsNoTracking()
            .Where(r => r.ContextId == contextId)
            .Select(r => r.RequesterUserId)
            .Distinct()
            .ToListAsync();

        return participants
            .Concat(committers)
            .Concat(activators)
            .Concat(requesters)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToHashSet();
    }

    private List<DeviceWelcomeDto> StoreWelcomes(
        List<DeviceWelcomeDto> welcomes,
        string contextId,
        string? conversationId,
        string? channelId,
        int generation,
        long epoch,
        IReadOnlySet<string>? allowedUserIds)
    {
        var stored = new List<DeviceWelcomeDto>();

        foreach (var welcome in welcomes)
        {
            if (welcome.Welcome is null || welcome.Welcome.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(welcome.DeviceId) || string.IsNullOrWhiteSpace(welcome.UserId)) continue;
            if (allowedUserIds is not null && !allowedUserIds.Contains(welcome.UserId)) continue;

            ctx.PendingWelcomes.Add(PendingWelcome.Create(new CreatePendingWelcomeParams
            {
                ContextId = contextId,
                ConversationId = conversationId,
                ChannelId = channelId,
                UserId = welcome.UserId,
                DeviceId = welcome.DeviceId,
                Welcome = welcome.Welcome,
                Generation = generation,
                Epoch = epoch,
            }));

            stored.Add(welcome);
        }

        return stored;
    }

    private async Task PushWelcomes(List<DeviceWelcomeDto> welcomes, string contextId)
    {
        // Addressed to the one device holding the matching leaf, not to every session the user has
        // open - the fetch behind this push is device-scoped, so waking a user's other devices only
        // costs them a round-trip that can never return anything.
        foreach (var welcome in welcomes)
        {
            await hub.Clients
                .Group(EchoRealtimeHub.DeviceGroup(welcome.UserId, welcome.DeviceId))
                .SendAsync("conversation.Welcome", contextId);
        }
    }

    /// <summary>
    /// Tells the context that a commit landed, so every device fetches and applies it.
    /// </summary>
    private async Task NotifyCommit(
        string contextId,
        string? conversationId,
        string? channelId,
        int generation,
        PublishMlsCommitDto dto,
        IReadOnlyCollection<string> notifyUserIds)
    {
        if (channelId is not null)
        {
            await bus.PublishAsync(new ChannelMlsCommitPublished
            {
                ChannelId = channelId,
                Generation = generation,
                Epoch = dto.Epoch,
                SenderDeviceId = dto.SenderDeviceId,
                IsProposal = dto.IsProposal,
            });
            return;
        }

        if (notifyUserIds.Count == 0) return;

        await hub.Clients.Users(notifyUserIds).SendAsync("conversation.MlsCommit", new
        {
            contextId,
            conversationId,
            channelId,
            generation,
            epoch = dto.Epoch,
            senderDeviceId = dto.SenderDeviceId,
            isProposal = dto.IsProposal,
        });
    }

    /// <summary>Announces every device this commit let into the group.</summary>
    private async Task AnnounceAdmissionsAsync(
        List<MlsJoinRequest> admitted,
        string contextId,
        string? conversationId,
        string? channelId,
        int generation,
        IReadOnlyCollection<string> notifyUserIds)
    {
        foreach (var request in admitted)
        {
            var autoAdmitted = !request.RequiresManualApproval && request.Approvals.Count == 0;

            var payload = new
            {
                contextId,
                conversationId,
                channelId,
                generation,
                userId = request.RequesterUserId,
                deviceId = request.RequesterDeviceId,
                signatureKeyFingerprint = request.SignatureKeyFingerprint,
                autoAdmitted,
            };

            if (autoAdmitted)
            {
                await hub.Clients.User(request.RequesterUserId).SendAsync("identity.DeviceAdmitted", payload);
            }

            // The conversation's own timeline entry: everyone in the room can see that the roster
            // gained a device, which is what makes an unexpected one noticeable to the other party
            // as well as to the owner.
            if (notifyUserIds.Count > 0)
            {
                await hub.Clients.Users(notifyUserIds).SendAsync("conversation.MlsDeviceAdmitted", payload);
            }
        }
    }

    /// <summary>Tells everyone in the context that encryption was turned on or off.</summary>
    private async Task NotifyStateChanged(
        string contextId, string? channelId, string? conversationId, bool encrypted, int generation, string userId)
    {
        if (channelId is not null)
        {
            await bus.PublishAsync(new ChannelMlsStateChanged
            {
                ChannelId = channelId,
                Encrypted = encrypted,
                Generation = generation,
                ChangedByUserId = userId,
            });
            return;
        }

        if (conversationId is null) return;

        var audience = await ctx.Members
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.UserId)
            .ToListAsync();

        if (audience.Count == 0) return;

        await hub.Clients.Users(audience).SendAsync("conversation.MlsStateChanged", new
        {
            contextId,
            channelId,
            conversationId,
            encrypted,
            generation,
        });
    }
}
