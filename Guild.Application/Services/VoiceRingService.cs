using Echo.Realtime;
using Echo.Voice.Rooms;
using Guild.Application.Bus.Events.Voice;
using Guild.Application.Models;
using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>Every way asking somebody into a voice channel can end.</summary>
public enum VoiceRingOutcome
{
    Created,

    /// <summary>This inviter already has a live ring out to this target, into this channel. The
    /// existing one is returned unchanged and nothing is re-sent.</summary>
    AlreadyPending,

    SelfRing,
    ChannelNotFound,
    NotAVoiceChannel,
    InviterNotInChannel,
    TargetNotAMember,

    /// <summary>Either the target cannot see the channel or cannot connect to it.</summary>
    TargetCannotJoinChannel,

    /// <summary>A block exists in one direction or the other, or could not be resolved.</summary>
    Unavailable,

    TargetAlreadyInChannel,
    Throttled,
}

/// <summary>What the caller gets back.</summary>
public readonly record struct VoiceRingResult(
    VoiceRingOutcome Outcome, VoiceRing? Ring, string? Refusal, TimeSpan RetryAfter)
{
    public static VoiceRingResult Refuse(VoiceRingOutcome outcome) => new(outcome, null, null, TimeSpan.Zero);
}

/// <summary>
/// The ephemeral "come and join me in here" ring: who may send one, what happens to it, and who is
/// told.
/// </summary>
public class VoiceRingService(
    MicroserviceContext db,
    GuildPermissionService permissions,
    VoiceRoomStore rooms,
    VoiceRingStore store,
    VoiceRingThrottle throttle,
    BlockCache blocks,
    NotificationResolutionService notifications,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus,
    ILogger<VoiceRingService> logger)
{
    public const string IncomingEvent = "guild.VoiceRingIncoming";
    public const string SentEvent = "guild.VoiceRingSent";
    public const string ResolvedEvent = "guild.VoiceRingResolved";
    public const string DismissedEvent = "guild.VoiceRingDismissed";

    /// <summary>Swappable so a test can watch a ring lapse.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    private static VoiceRoomKey Room(string channelId) => VoiceRoomKey.Channel(channelId);

    /// <summary>
    /// Asks <paramref name="targetUserId"/> into <paramref name="channelId"/>, on behalf of
    /// somebody who is already sitting in it.
    /// </summary>
    public async Task<VoiceRingResult> RingAsync(
        string inviterId, string? inviterDeviceId, string guildId, string channelId, string targetUserId,
        CancellationToken ct = default)
    {
        if (string.Equals(inviterId, targetUserId, StringComparison.Ordinal))
            return VoiceRingResult.Refuse(VoiceRingOutcome.SelfRing);

        var channel = await db.Channels
            .AsNoTracking()
            .Select(c => new { c.Id, c.GuildId, c.Type, c.Name })
            .FirstOrDefaultAsync(c => c.Id == channelId && c.GuildId == guildId, ct);

        if (channel is null) return VoiceRingResult.Refuse(VoiceRingOutcome.ChannelNotFound);
        if (channel.Type != Guild.Domain.Enums.ChannelType.Voice)
            return VoiceRingResult.Refuse(VoiceRingOutcome.NotAVoiceChannel);

        // Membership of the room, not merely permission to connect to it.
        var room = await rooms.LoadAsync(Room(channelId), ct);
        if (room?.Find(inviterId) is null) return VoiceRingResult.Refuse(VoiceRingOutcome.InviterNotInChannel);

        var existing = await store.PendingForTargetAsync(targetUserId, ct);

        var samePlace = existing.FirstOrDefault(r =>
            r.InviterId == inviterId && r.ChannelId == channelId);
        if (samePlace is not null)
            return new VoiceRingResult(VoiceRingOutcome.AlreadyPending, samePlace, null, TimeSpan.Zero);

        var verdict = await throttle.TryAcquireAsync(inviterId, targetUserId, ct);
        if (!verdict.Allowed)
            return new VoiceRingResult(VoiceRingOutcome.Throttled, null, verdict.Reason, verdict.RetryAfter);

        var member = await db.GuildMembers
            .AsNoTracking()
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == targetUserId, ct);

        if (member is null) return VoiceRingResult.Refuse(VoiceRingOutcome.TargetNotAMember);

        var blockView = await blocks.GetAsync([inviterId, targetUserId], ct);
        if (blockView.AreBlocked(inviterId, targetUserId))
            return VoiceRingResult.Refuse(VoiceRingOutcome.Unavailable);

        // ViewChannel and Connect both, and both about the target rather than the inviter.
        if (!await permissions.CanUserPerformActionAsync(targetUserId, channelId, Permissions.ViewChannel)
            || !await permissions.CanUserPerformActionAsync(targetUserId, channelId, Permissions.Connect))
            return VoiceRingResult.Refuse(VoiceRingOutcome.TargetCannotJoinChannel);

        if (room.Find(targetUserId) is not null)
        {
            // A benign race - they walked in while the inviter was clicking - so the budget goes
            // back.
            await throttle.RefundAsync(inviterId, targetUserId, ct);
            return VoiceRingResult.Refuse(VoiceRingOutcome.TargetAlreadyInChannel);
        }

        // One person may not hold you to two invitations at once.
        foreach (var stale in existing.Where(r => r.InviterId == inviterId))
            await ResolveInternalAsync(stale.Id, VoiceRingStatus.Cancelled, VoiceRingReason.Superseded, null, ct);

        var now = Now;
        var ring = new VoiceRing
        {
            Id = VoiceRing.GenerateId(),
            GuildId = guildId,
            ChannelId = channelId,
            InviterId = inviterId,
            TargetUserId = targetUserId,
            InviterDeviceId = inviterDeviceId,
            CreatedAt = now,
            ExpiresAt = now.Add(VoiceRing.Ttl),
        };

        await store.CreateAsync(ring, ct);

        // Scheduled per ring rather than swept: the client is visibly counting down to this instant,
        // and a sweep interval would let the card outlive its own timer.
        await bus.ScheduleAsync(new VoiceRingTimeoutCheck { RingId = ring.Id }, VoiceRing.Ttl);

        var inviter = await ProfileAsync(inviterId, ct);
        await AnnounceIncomingAsync(ring, channel.Name, inviter, room, ct);
        await RequestPushAsync(ring, member.Id, channel.Name, inviter, ct);
        await RequestDirectMessageAsync(ring, channel.Name, ct);
        await PublishForBotsAsync(ring, ct);

        return new VoiceRingResult(VoiceRingOutcome.Created, ring, null, TimeSpan.Zero);
    }

    /// <summary>The target says yes, the target says no, or the inviter takes it back.</summary>
    public async Task<VoiceRingTransition> ResolveAsync(
        string ringId, VoiceRingStatus status, string? reason, string? deviceId,
        CancellationToken ct = default)
    {
        var transition = await ResolveInternalAsync(ringId, status, reason, deviceId, ct);
        if (transition is { Transitioned: true, Ring: { } ring } && status == VoiceRingStatus.Declined)
        {
            var cooldown = await throttle.RecordDeclineAsync(ring.InviterId, ring.TargetUserId, ct);
            logger.LogDebug(
                "Voice ring {RingId} declined; {InviterId} may not ring {TargetUserId} again for {Cooldown}",
                ring.Id, ring.InviterId, ring.TargetUserId, cooldown);
        }

        return transition;
    }

    /// <summary>Whether the target could still act on a ring pointing at this channel.</summary>
    public async Task<bool> CanTargetStillJoinAsync(string targetUserId, string channelId) =>
        await permissions.CanUserPerformActionAsync(targetUserId, channelId, Permissions.ViewChannel)
        && await permissions.CanUserPerformActionAsync(targetUserId, channelId, Permissions.Connect);

    /// <summary>
    /// Tells exactly one device to stop showing a ring it has already lost the race for.
    /// </summary>
    public Task DismissDeviceAsync(VoiceRing ring, string deviceId, CancellationToken ct = default) =>
        hub.Clients.Group(EchoRealtimeHub.DeviceGroup(ring.TargetUserId, deviceId))
            .SendAsync(DismissedEvent, new
            {
                ringId = ring.Id,
                deviceId,
                status = ring.Status.ToString(),
                reason = ring.Reason,
            }, ct);

    /// <summary>Closes every ring <paramref name="inviterId"/> has outstanding into
    /// <paramref name="channelId"/>, because they have left it. Called from the leave path, not
    /// scheduled: an invitation that says "join me" stops being true the moment its author walks
    /// out.</summary>
    public Task CancelForInviterLeftAsync(string channelId, string inviterId, CancellationToken ct = default) =>
        CancelInChannelAsync(channelId, r => r.InviterId == inviterId, VoiceRingReason.InviterLeft, ct);

    /// <summary>
    /// Closes every ring asking <paramref name="targetUserId"/> into <paramref name="channelId"/>,
    /// because they are now in it.
    /// </summary>
    public Task CancelForTargetJoinedAsync(string channelId, string targetUserId, CancellationToken ct = default) =>
        CancelInChannelAsync(channelId, r => r.TargetUserId == targetUserId, VoiceRingReason.TargetJoined, ct);

    private async Task CancelInChannelAsync(
        string channelId, Func<VoiceRing, bool> match, string reason, CancellationToken ct)
    {
        foreach (var ring in (await store.PendingForChannelAsync(channelId, ct)).Where(match))
            await ResolveInternalAsync(ring.Id, VoiceRingStatus.Cancelled, reason, null, ct);
    }

    private async Task<VoiceRingTransition> ResolveInternalAsync(
        string ringId, VoiceRingStatus status, string? reason, string? deviceId, CancellationToken ct)
    {
        var transition = await store.ResolveAsync(ringId, status, reason, deviceId, ct);
        if (transition is not { Transitioned: true, Ring: { } ring }) return transition;

        await AnnounceResolvedAsync(ring, ct);
        await CancelPushAsync(ring, ct);
        await PublishForBotsAsync(ring, ct);

        return transition;
    }

    // ── Notification ─────────────────────────────────────────────────────────

    private async Task AnnounceIncomingAsync(
        VoiceRing ring, string channelName, ProfileDto? inviter, VoiceRoom room, CancellationToken ct)
    {
        var payload = new
        {
            ringId = ring.Id,
            guildId = ring.GuildId,
            channelId = ring.ChannelId,
            channelName,
            inviterId = ring.InviterId,
            inviterName = inviter?.UserName,
            inviterAvatarUrl = inviter?.AvatarUrl,
            targetUserId = ring.TargetUserId,
            createdAt = ring.CreatedAt,
            expiresAt = ring.ExpiresAt,
            expiresInSeconds = (int)Math.Max(0, (ring.ExpiresAt - Now).TotalSeconds),
            // Who is already in there, so the card can show faces rather than a channel name.
            participantUserIds = room.AllUserIds(),
        };

        await hub.Clients.User(ring.TargetUserId).SendAsync(IncomingEvent, payload, ct);

        // The inviter's other devices, so a second window does not offer to send the invitation
        // that is already out. Their own request got the ring back in its response.
        await hub.Clients.User(ring.InviterId).SendAsync(SentEvent, payload, ct);
    }

    /// <summary>
    /// One event for every terminal transition, carrying the status rather than one event name per
    /// status.
    /// </summary>
    private Task AnnounceResolvedAsync(VoiceRing ring, CancellationToken ct) =>
        hub.Clients.Users(ring.TargetUserId, ring.InviterId).SendAsync(ResolvedEvent, new
        {
            ringId = ring.Id,
            guildId = ring.GuildId,
            channelId = ring.ChannelId,
            inviterId = ring.InviterId,
            targetUserId = ring.TargetUserId,
            status = ring.Status.ToString(),
            reason = ring.Reason,
            resolvedAt = ring.ResolvedAt,
            resolvedByDeviceId = ring.ResolvedByDeviceId,
        }, ct);

    /// <summary>Asks Messaging to buzz the target's phone.</summary>
    private async Task RequestPushAsync(
        VoiceRing ring, string memberId, string channelName, ProfileDto? inviter, CancellationToken ct)
    {
        var setting = await notifications.ResolveForChannelAsync(ring.ChannelId, memberId);
        if (setting.IsMuted || !setting.MobilePush) return;

        var name = string.IsNullOrWhiteSpace(inviter?.UserName) ? "Voice invite" : inviter.UserName;

        await bus.PublishAsync(new VoiceRingPushRequested
        {
            RingId = ring.Id,
            GuildId = ring.GuildId,
            ChannelId = ring.ChannelId,
            TargetUserId = ring.TargetUserId,
            InviterId = ring.InviterId,
            InviterAvatarUrl = inviter?.AvatarUrl,
            ExpiresInSeconds = (int)Math.Max(0, (ring.ExpiresAt - Now).TotalSeconds),
            Title = name,
            Body = $"Asked you to join {channelName}.",
            BodyLocKey = VoiceLocKeys.InviteBody,
            BodyLocArgs = [channelName],
        });
    }

    /// <summary>
    /// Asks Messaging to leave the invitation in the two people's direct conversation.
    /// </summary>
    private async Task RequestDirectMessageAsync(VoiceRing ring, string channelName, CancellationToken ct) =>
        await bus.PublishAsync(new VoiceRingDirectMessageRequested
        {
            RingId = ring.Id,
            GuildId = ring.GuildId,
            ChannelId = ring.ChannelId,
            ChannelName = channelName,
            InviterId = ring.InviterId,
            TargetUserId = ring.TargetUserId,
            // Stamped rather than converted implicitly.
            ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(ring.ExpiresAt, DateTimeKind.Utc), TimeSpan.Zero),
        });

    /// <summary>Takes the notification back off the target's lock screen.</summary>
    private async Task CancelPushAsync(VoiceRing ring, CancellationToken ct) =>
        await bus.PublishAsync(new VoiceRingPushRequested
        {
            RingId = ring.Id,
            GuildId = ring.GuildId,
            ChannelId = ring.ChannelId,
            TargetUserId = ring.TargetUserId,
            InviterId = ring.InviterId,
            Title = string.Empty,
            Cancel = true,
            CancelReason = ring.Reason ?? ring.Status.ToString(),
            ExcludeDeviceId = ring.ResolvedByDeviceId,
        });

    private async Task PublishForBotsAsync(VoiceRing ring, CancellationToken ct) =>
        await bus.PublishAsync(new VoiceRingForBots
        {
            RingId = ring.Id,
            GuildId = ring.GuildId,
            ChannelId = ring.ChannelId,
            InviterId = ring.InviterId,
            TargetUserId = ring.TargetUserId,
            Status = ring.Status.ToString(),
            Reason = ring.Reason,
            OccurredAt = ring.ResolvedAt ?? ring.CreatedAt,
        });

    private async Task<ProfileDto?> ProfileAsync(string userId, CancellationToken ct)
    {
        try
        {
            var response = await bus.InvokeAsync<GetProfileByUserIdResponse>(
                new GetProfileByUserIdRequest { UserId = userId }, ct);
            return response.Profile;
        }
        catch (Exception e)
        {
            // A nameless invitation is worse than no invitation only if the client cannot fill the
            // gap, and it can - the payload carries the inviter's id precisely so it never has to
            // trust the name that was frozen into it.
            logger.LogWarning(e, "Could not resolve the profile of voice-ring inviter {UserId}", userId);
            return null;
        }
    }
}
