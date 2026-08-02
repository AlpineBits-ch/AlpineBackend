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
///
/// <para>The two differ only in who is allowed to do what - conversation membership versus channel
/// permissions - so authorization stays in the endpoints and everything below it lives here once.
/// Nothing in this class knows or cares which kind of context it is holding.</para>
/// </summary>
public class MlsGroupService(
    MicroserviceContext ctx,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus,
    MlsJoinRequestService joinRequests)
{
    /// <summary>Minimum spacing between toggles of the same context.
    ///
    /// <para>Every toggle forces every client to tear down or stand up group state, and a client
    /// that is mid-send when it happens gets rejected and has to retry. Allowing an admin to flip
    /// the switch repeatedly in a few seconds means no client ever reaches a steady state - the
    /// cooldown is what makes "toggle back and forth" converge instead of thrash.</para></summary>
    public static readonly TimeSpan ToggleCooldown = TimeSpan.FromSeconds(30);

    /// <summary>How long a commit stays fetchable. A device offline longer than this cannot replay
    /// its way forward and has to rejoin by external commit against the stored GroupInfo.</summary>
    public static readonly TimeSpan CommitRetention = TimeSpan.FromDays(30);

    /// <summary>Cap on one catch-up page. A device further behind pages again with a higher
    /// sinceEpoch; it never silently receives a truncated view of history.</summary>
    public const int MaxCommitPageSize = 200;

    /// <summary>Ceiling on how many Welcomes one call may park.
    ///
    /// <para>The publisher chooses the recipients, so without a bound a single authenticated member
    /// could write unbounded rows addressed to arbitrary users. Well above any real group's device
    /// count - a 200-member conversation with three devices each still fits.</para></summary>
    public const int MaxWelcomesPerCall = 1000;

    public Task<MlsGroupGeneration?> GetActiveGenerationAsync(string contextId) =>
        ctx.MlsGroupGenerations
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

    /// <summary>
    /// Whether the server has any evidence this user has ever been inside this MLS group.
    ///
    /// <para>The server holds no group state, so it cannot read the roster - but it does hold the
    /// three artifacts a member necessarily leaves: they switched encryption on, or a Welcome was
    /// addressed to one of their devices, or they published a commit. Any of the three means they
    /// were let in by somebody who could actually let them in.</para>
    ///
    /// <para>This is deliberately an <i>over</i>-approximation of membership - a removed member still
    /// has their old Welcome row - and that is the right direction. It is used to withhold a
    /// GroupInfo and to gate commit publication, where the alternative to a slightly-too-wide rule is
    /// no rule at all: both were previously reachable with nothing but <c>ViewChannel</c>.</para>
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

    /// <summary>
    /// A context's encryption state.
    ///
    /// <para><b><c>MlsGroupInfo</c> is withheld from callers with no evidence of membership.</b> A
    /// GroupInfo is everything needed to external-commit into the group, and this route required only
    /// <c>ViewChannel</c> - so any channel viewer could walk straight past the join-request review
    /// that exists precisely to decide who gets in. The client-side defence §H.4 describes is gated
    /// on <c>MlsPolicy.CertificateEnforcement</c>, which starts at Observe and which the server
    /// itself serves, so it is not a defence against the server at all.</para>
    ///
    /// <para>Everything else stays visible on <c>ViewChannel</c>, because a reader genuinely needs to
    /// know the context is encrypted and which generation is live - that is what tells it to ask for
    /// admission rather than render the room as broken.</para>
    /// </summary>
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
            // Past generations are listed because their messages are still in the channel. A client
            // needs to know they existed to explain why a stretch of history it cannot read is
            // there, rather than rendering it as corrupt.
            Generations = generations.SelectFacets<MlsGroupGeneration, MlsGenerationDto>().ToList(),
        };
    }

    /// <summary>
    /// Turns encryption on by minting the next generation. Existing plaintext history is untouched
    /// and stays readable - this is a boundary in the context's timeline, not a conversion.
    /// </summary>
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

        // Conversations carry their MLS state on the aggregate as well. Kept in sync rather than
        // migrated away from, because existing clients read encryption state off the conversation
        // DTO and would otherwise see an encrypted conversation as plaintext.
        //
        // Resolved *before* anything is mutated. This used to sit after the generation row was
        // added, and Wolverine's transactional middleware commits whatever is on the context when
        // the endpoint returns - so the NotFound path persisted a half-applied generation for a
        // conversation that does not exist, permanently marking the context encrypted with no group
        // behind it.
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

    /// <summary>
    /// Turns encryption off by terminating the active generation. Messages sent under it stay
    /// ciphertext - nothing is decrypted or rewritten - so the caller is told which generation was
    /// terminated and can say plainly which stretch of history is now readable only by devices that
    /// still hold that group.
    /// </summary>
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

        // Welcomes for the terminated generation are worthless - the group is gone. Consuming them
        // stops a device that was offline during the whole encrypted stretch from waking up, joining
        // a dead group, and reporting itself as encrypted when the context no longer is.
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
        // needed only ViewChannel - so somebody who had never been in the group could move the epoch
        // to a value no member could reach. Membership is not something the server can read, but the
        // artifacts of having been admitted are.
        if (!await HasGroupParticipationAsync(contextId, active.Generation, userId))
        {
            return MlsOperationResult.BadRequest(
                "You are not in this MLS group. Being able to see the context is not being in its "
                + "group - request admission first.");
        }

        // The declared kind, checked against the payload. See MlsMessageInspector: a member used to
        // be able to wedge a group permanently by publishing a proposal, or noise, flagged as a
        // commit at epoch+1.
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
        //
        // A client that published a commit and never saw the response is holding a merged local
        // group at epoch N+1 with no way to tell whether the server took it. Answering 409 sends it
        // down the "someone else won, discard and re-fetch" path - discarding a commit that *is* the
        // group's history and forking itself off permanently. Matched on sender device, generation,
        // epoch and the exact bytes, so this can only ever recognise the caller's own work.
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

        // A proposal is not a commit. Processing one does not advance any client's MLS epoch, so the
        // server must not advance the group's either: doing so left every client's catch-up loop
        // permanently one epoch behind a number that could never be reached, and mobile's
        // `while(true)` drain never terminated. It rides the same transport so devices still receive
        // it in order; it simply does not claim the slot.
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

        // Belt to the unique index's braces. The index resolves two genuinely concurrent publishers;
        // this turns the common non-racing case into a clean conflict rather than a DbUpdateException.
        // Proposals are exempt on both sides - they do not occupy an epoch, and the index that
        // enforces this is filtered to exclude them.
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
        //
        // The ids are checked against what this commit actually carried, not taken on the
        // publisher's word - see FulfilAsync. `storedWelcomes` rather than `dto.Welcomes`, so a
        // Welcome that was rejected as addressed outside the roster cannot vouch for an admission.
        var kind = conversationId is not null ? MlsContextKind.Conversation : MlsContextKind.Channel;
        var required = await joinRequests.RequiredApprovalsFor(contextId, active.Generation, kind);

        var admitted = await joinRequests.FulfilAsync(
            contextId,
            dto.FulfilledJoinRequestIds,
            storedWelcomes.Select(w => (w.UserId, w.DeviceId)).ToList(),
            required,
            now);

        await PruneExpiredCommitsAsync(contextId, now);

        // Save before the fanout, not after. Wolverine's transactional middleware would otherwise
        // commit only once the endpoint returns, and a client acting on the nudge in between would
        // fetch the commit list and not find the commit it was just told about.
        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsEpochUniqueViolation(ex))
        {
            // The read-then-write above is inherently racy; the database is what actually decides.
            // Losing that race is a 409 like any other lost race - it used to surface as a 500, and
            // clients only retry on 409, so the change was silently dropped and the member's roster
            // update never happened.
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
            // No generation named means "the one that is live now". Legacy callers predate
            // generations entirely and are always asking about the current group.
            var active = await ctx.MlsGroupGenerations
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

            // Nothing is live: fall back to the most recent generation rather than to "all of them".
            // Leaving the filter off returned every generation's commits interleaved by epoch, and a
            // client applying that sequence is feeding one group's commits to another - which is a
            // fork, not a decrypt failure it can recover from. A context that has never been
            // encrypted has no generations at all and correctly yields nothing.
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

    /// <summary>Per-artifact size ceilings.
    ///
    /// <para>Only the backup blob had one, so a member could park thirty megabytes per commit -
    /// retained for thirty days, and multiplied by <see cref="MaxWelcomesPerCall"/> for the
    /// Welcomes. These are far above any real MLS artifact: a commit in a large group is kilobytes,
    /// a Welcome is a few hundred bytes per recipient.</para></summary>
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

    /// <summary>
    /// Checks the payload against what the caller says it is.
    ///
    /// <para>Three things, all readable without any group secret (RFC 9420 frames the group id, the
    /// epoch and the content type outside the encrypted portion):</para>
    ///
    /// <list type="bullet">
    /// <item>It parses as an <c>MLSMessage</c> at all, and as a message rather than a Welcome, a
    /// GroupInfo or a KeyPackage. Noise flagged <c>isProposal: false</c> at <c>epoch+1</c> was the
    /// cheapest way to wedge a group forever.</item>
    /// <item>Its content type matches <c>isProposal</c>. A proposal does not advance any client's
    /// epoch, so the server advancing the group's on a proposal mislabelled as a commit leaves every
    /// client one epoch behind a number nothing can reach.</item>
    /// <item>Its group id is this generation's. A commit for a different group applied to this one
    /// forks everybody.</item>
    /// </list>
    ///
    /// <para>The framing epoch is deliberately <b>not</b> checked against <c>dto.Epoch</c>: for an
    /// ordinary commit it is the epoch the commit was built <i>in</i> (one below), for a proposal it
    /// is the current one, and for an external commit it is the epoch of the GroupInfo the joiner
    /// built against. Three different relationships, and getting the arithmetic wrong would reject
    /// legitimate traffic - which is a worse failure than the one being closed here.</para>
    /// </summary>
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

    /// <summary>
    /// Drops commits past the retention window, except any a device still needs.
    ///
    /// <para>An unconsumed Welcome is a device that has not joined yet and will start replaying from
    /// the epoch that Welcome landed it on. Pruning below that point hands it an empty catch-up list,
    /// which is indistinguishable from "you are up to date" - so it silently believes it is in sync
    /// with a group it cannot read. Commits at or below the oldest outstanding Welcome's epoch are
    /// already behind every joiner and safe to drop; everything above it is kept until the Welcome is
    /// acknowledged or the generation is terminated.</para>
    /// </summary>
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

    /// <summary>
    /// Who a Welcome may be addressed to.
    ///
    /// <para>Conversation membership lives here, so a Welcome for someone outside the conversation is
    /// rejected rather than parked - the publisher picks the recipients, and without this any member
    /// could write rows against arbitrary user ids.</para>
    ///
    /// <para><b>Channel membership lives in Guild, and "bounded by count alone" was not a bound.</b>
    /// A channel commit could address Welcomes to any user id at all. Rather than ask Guild to
    /// resolve a permission-aware viewer roster on every commit - which is a per-recipient round trip
    /// on the hot path - the rule is drawn from what Messaging already knows about the context:
    /// a recipient must either have asked to be let in, or have been in it before. Both are exactly
    /// the population a legitimate Add commit mints Welcomes for.</para>
    ///
    /// <para>Returns null only for the generation-activating call, where there is by construction no
    /// prior participation to point at and the caller already holds <c>ManageChannel</c>.</para>
    /// </summary>
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
    ///
    /// <para>Channels used to be told by nobody: the endpoint passed an empty notify list and the
    /// comment there pointed at a sibling path inside this class that did not exist. A channel commit
    /// therefore reached exactly the device that published it, and every other member stayed on the
    /// old epoch until something unrelated made it re-fetch. Guild owns channel membership, so the
    /// channel case takes the bus exactly as the state-change announcement already does.</para>
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

    /// <summary>
    /// Announces every device this commit let into the group.
    ///
    /// <para><b>Automatic admission is silent admission, never silent notification.</b> A device that
    /// got in on cryptographic proof alone did so without any human on the account being asked, which
    /// is the point - but it is also indistinguishable, from the owner's side, from someone who knew
    /// the password adding a device of their own. So the owner's other devices are told immediately,
    /// with the signature-key fingerprint, and so is the conversation. Being able to notice is the
    /// entire compensating control for not being asked.</para>
    ///
    /// <para>Requests that a human did approve are announced to the conversation too, but not pushed
    /// at the owner as a security event - somebody on the account already saw and allowed it.</para>
    /// </summary>
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

    /// <summary>
    /// Tells everyone in the context that encryption was turned on or off.
    ///
    /// <para>Conversation membership is known here, so that push goes direct. A channel's audience
    /// is not - Guild owns it - so that one goes over the bus and Guild fans it out, exactly as
    /// channel messages already do rather than duplicating membership resolution here.</para>
    /// </summary>
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
