using Echo.Realtime;
using Messaging.Contracts.Bus.Events;
using Facet.Extensions;
using Messaging.Application.Dtos.Request;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
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
public class MlsGroupService(MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub, IMessageBus bus)
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

    public Task<MlsGroupGeneration?> GetActiveGenerationAsync(string contextId) =>
        ctx.MlsGroupGenerations
            .FirstOrDefaultAsync(g => g.ContextId == contextId && g.State == MlsGenerationState.Active);

    public async Task<MlsContextStateDto> GetStateAsync(string contextId)
    {
        var generations = await ctx.MlsGroupGenerations
            .AsNoTracking()
            .Where(g => g.ContextId == contextId)
            .OrderBy(g => g.Generation)
            .ToListAsync();

        var active = generations.FirstOrDefault(g => g.State == MlsGenerationState.Active);

        return new MlsContextStateDto
        {
            ContextId = contextId,
            Encrypted = active is not null,
            ActiveGeneration = active?.Generation,
            Epoch = active?.Epoch,
            MlsGroupId = active?.MlsGroupId,
            MlsGroupInfo = active?.MlsGroupInfo,
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

        StoreWelcomes(dto.Welcomes, contextId, conversationId, channelId, generation.Generation, dto.Epoch);

        // Conversations carry their MLS state on the aggregate as well. Kept in sync rather than
        // migrated away from, because existing clients read encryption state off the conversation
        // DTO and would otherwise see an encrypted conversation as plaintext.
        if (conversationId is not null)
        {
            var conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null) return MlsOperationResult.NotFound("Conversation not found");

            conversation.EncryptionState = ChannelEncryptionState.Encrypted;
            conversation.MlsGroupId = dto.MlsGroupId;
            conversation.MlsEpoch = dto.Epoch;
            conversation.MlsGroupInfo = dto.MlsGroupInfo;
        }

        await ctx.SaveChangesAsync();

        await NotifyStateChanged(contextId, channelId, conversationId, encrypted: true, generation.Generation, userId);
        await PushWelcomes(dto.Welcomes, contextId);

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

        active.Terminate(userId, now);

        if (conversationId is not null)
        {
            var conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null) return MlsOperationResult.NotFound("Conversation not found");

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

        if (dto.Epoch != active.Epoch + 1)
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
        var epochTaken = await ctx.MlsCommits.AnyAsync(c =>
            c.ContextId == contextId && c.Generation == active.Generation && c.Epoch == dto.Epoch);
        if (epochTaken)
        {
            return MlsOperationResult.Conflict(new MlsEpochConflictDto
            {
                CurrentEpoch = active.Epoch,
                RejectedEpoch = dto.Epoch,
                CurrentGeneration = active.Generation,
                RejectedGeneration = active.Generation,
                Reason = "Another member already committed this epoch.",
            });
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
        }));

        active.Epoch = dto.Epoch;
        active.UpdatedAt = now;
        if (dto.GroupInfo is { Length: > 0 }) active.MlsGroupInfo = dto.GroupInfo;

        if (conversationId is not null)
        {
            var conversation = await ctx.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is not null)
            {
                conversation.MlsEpoch = dto.Epoch;
                if (dto.GroupInfo is { Length: > 0 }) conversation.MlsGroupInfo = dto.GroupInfo;
            }
        }

        StoreWelcomes(dto.Welcomes, contextId, conversationId, channelId, active.Generation, dto.Epoch);

        var cutoff = now - CommitRetention;
        var expired = await ctx.MlsCommits
            .Where(c => c.ContextId == contextId && c.CreatedAt < cutoff)
            .ToListAsync();
        if (expired.Count > 0) ctx.MlsCommits.RemoveRange(expired);

        // Save before the fanout, not after. Wolverine's transactional middleware would otherwise
        // commit only once the endpoint returns, and a client acting on the nudge in between would
        // fetch the commit list and not find the commit it was just told about.
        await ctx.SaveChangesAsync();

        await hub.Clients.Users(notifyUserIds).SendAsync("conversation.MlsCommit", new
        {
            contextId,
            conversationId,
            channelId,
            generation = active.Generation,
            epoch = dto.Epoch,
            senderDeviceId = dto.SenderDeviceId,
        });

        await PushWelcomes(dto.Welcomes, contextId);

        return MlsOperationResult.Ok(new MlsCommitPublishedDto
        {
            ContextId = contextId,
            ConversationId = conversationId,
            Generation = active.Generation,
            Epoch = dto.Epoch,
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
            resolved = active?.Generation;
        }

        var query = ctx.MlsCommits.AsNoTracking().Where(c => c.ContextId == contextId && c.Epoch > sinceEpoch);
        if (resolved is not null) query = query.Where(c => c.Generation == resolved);

        var commits = await query
            .OrderBy(c => c.Epoch)
            .Take(MaxCommitPageSize)
            .ToListAsync();

        return commits.SelectFacets<MlsCommit, MlsCommitResponseDto>().ToList();
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

    private void StoreWelcomes(
        List<DeviceWelcomeDto> welcomes,
        string contextId,
        string? conversationId,
        string? channelId,
        int generation,
        long epoch)
    {
        foreach (var welcome in welcomes)
        {
            if (welcome.Welcome is null || welcome.Welcome.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(welcome.DeviceId) || string.IsNullOrWhiteSpace(welcome.UserId)) continue;

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
        }
    }

    private async Task PushWelcomes(List<DeviceWelcomeDto> welcomes, string contextId)
    {
        // Addressed to the one device holding the matching leaf, not to every session the user has
        // open - the fetch behind this push is device-scoped, so waking a user's other devices only
        // costs them a round-trip that can never return anything.
        foreach (var welcome in welcomes)
        {
            if (string.IsNullOrWhiteSpace(welcome.UserId) || string.IsNullOrWhiteSpace(welcome.DeviceId)) continue;
            await hub.Clients
                .Group(EchoRealtimeHub.DeviceGroup(welcome.UserId, welcome.DeviceId))
                .SendAsync("conversation.Welcome", contextId);
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
