using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Guild.Application.Controllers;
using Guild.Application.Dtos.Response;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Identity.Contracts.Bus.Response;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Dtos;

namespace Guild.Application.Bus.Events.Realtime;

/// <summary>
/// Presence + voice cleanup that used to live in GuildHub.OnConnectedAsync / OnDisconnectedAsync
/// and the per-pulse heartbeat callback. Driven now by the gateway hub's lifecycle commands.
/// </summary>
public class GuildLifecycleHandler
{
    private static readonly DistributedCacheEntryOptions ChannelCacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    // A brand-new connection defaults to Online (matches Discord's default) - there is no prior
    // presence entry to preserve a status from.
    public async Task Handle(UserConnected message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks,
        PrivacySettingsCache privacy, IDistributedCache cache, VoiceRoomStore rooms)
    {
        var updates = await RefreshPresenceAsync(message.UserId, microserviceContext, service,
            defaultStatus: nameof(OnlineStatus.Online));

        var settings = await privacy.GetAsync(message.UserId);

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub, blocks, settings);

        // A reconnect is the other half of the disconnect grace below.
        await RestoreVoiceLivenessAsync(message, cache, rooms);
    }

    // The gateway hub republishes this while the connection is alive (throttled), replacing the old
    // IConnectionHeartbeatFeature per-pulse refresh.
    public Task Handle(PresenceHeartbeat message, MicroserviceContext microserviceContext, GuildHydrateService service)
        => RefreshPresenceAsync(message.UserId, microserviceContext, service, defaultStatus: null);

    private static async Task<List<PresenceUpdate>> RefreshPresenceAsync(
        string userId, MicroserviceContext ctx, GuildHydrateService service, string? defaultStatus)
    {
        var members = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .ToListAsync();

        var updates = new List<PresenceUpdate>();

        foreach (var m in members)
        {
            var existing = await service.GetPresenceStateForMemberAsync(m.Id);
            var status = existing?.Status ?? defaultStatus ?? nameof(OnlineStatus.Online);

            await service.AddPresenceStateAsync(m.GuildId, new MemberPresenceState
            {
                MemberId = m.Id,
                UserId = m.UserId,
                Status = status,
                Activity = existing?.Activity,
                // Carried through for the same reason the status is: this runs on every heartbeat,
                // and anything not explicitly preserved here is silently erased every ~30 seconds.
                Activities = existing?.Activities,
                ClientStatus = existing?.ClientStatus,
                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            updates.Add(new PresenceUpdate(m.GuildId, status, existing?.Activities ?? []));
        }

        return updates;
    }

    /// <summary>One guild's worth of what changed, as it will go on the wire before projection.</summary>
    private readonly record struct PresenceUpdate(string GuildId, string Status, IReadOnlyList<ActivityDto> Activities);

    /// <summary>Fans a presence change out to the guilds the user is in.</summary>
    private static async Task BroadcastPresenceChangesAsync(string userId, List<PresenceUpdate> updates,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks, UserPrivacySettingsSummary privacy)
    {
        if (updates.Count == 0) return;

        var blockView = await blocks.GetAsync([userId]);

        foreach (var (guildId, status, activities) in updates)
        {
            var presence = await service.GetGuildPresenceAsync(guildId);
            var recipients = presence.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();

            var self = recipients.Where(id => string.Equals(id, userId, StringComparison.Ordinal)).ToList();
            var others = blockView.Reachable(userId,
                recipients.Where(id => !string.Equals(id, userId, StringComparison.Ordinal)));

            if (self.Count > 0)
            {
                await hub.Clients.Users(self).SendAsync("guild.PresenceChanged",
                    new
                    {
                        UserId = userId,
                        GuildId = guildId,
                        Status = status,
                        Activities = PresenceProjection.ProjectActivitiesFor(
                            activities, status, viewerIsSubject: true, privacy.ShareActivity, privacy.HiddenActivities),
                    });
            }

            if (others.Count > 0)
            {
                await hub.Clients.Users(others).SendAsync("guild.PresenceChanged",
                    new
                    {
                        UserId = userId,
                        GuildId = guildId,
                        Status = PresenceProjection.ProjectNameFor(status, viewerIsSubject: false),
                        // Projected against the *stored* status, not the one just projected above -
                        // the Hidden gate needs to see the truth to act on it, and by then the
                        // status on this line has already been flattened to Offline.
                        Activities = PresenceProjection.ProjectActivitiesFor(
                            activities, status, viewerIsSubject: false, privacy.ShareActivity, privacy.HiddenActivities),
                    });
            }
        }
    }

    public async Task Handle(UserStatusChanged message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks,
        PrivacySettingsCache privacy)
    {
        var members = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == message.UserId)
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .ToListAsync();

        var updates = new List<PresenceUpdate>();

        foreach (var m in members)
        {
            var existing = await service.GetPresenceStateForMemberAsync(m.Id);

            await service.AddPresenceStateAsync(m.GuildId, new MemberPresenceState
            {
                MemberId = m.Id,
                UserId = m.UserId,
                Status = message.Status,
                Activity = existing?.Activity,
                Activities = existing?.Activities,
                ClientStatus = existing?.ClientStatus,
                HeartbeatTimestamp = existing?.HeartbeatTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            updates.Add(new PresenceUpdate(m.GuildId, message.Status, existing?.Activities ?? []));
        }

        var settings = await privacy.GetAsync(message.UserId);

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub, blocks, settings);
    }

    /// <summary>A user's activity list changed.</summary>
    public async Task Handle(UserActivityChanged message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks,
        PrivacySettingsCache privacy)
    {
        var members = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == message.UserId)
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .ToListAsync();

        var live = new List<(string MemberId, string UserId, string GuildId, MemberPresenceState Presence)>();

        foreach (var m in members)
        {
            var existing = await service.GetPresenceStateForMemberAsync(m.Id);
            if (existing is null) continue;

            live.Add((m.Id, m.UserId, m.GuildId, existing));
        }

        if (live.Count == 0) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var priorActivities = live.SelectMany(l => l.Presence.Activities ?? []).ToList();
        var activities = MergeStartTimes(priorActivities, message.Activities, nowMs);

        var updates = new List<PresenceUpdate>(live.Count);

        foreach (var (memberId, userId, guildId, presence) in live)
        {
            await service.AddPresenceStateAsync(guildId, new MemberPresenceState
            {
                MemberId = memberId,
                UserId = userId,
                // Activity is not a status change: whatever the user had set - including Hidden -
                // is carried through untouched.
                Status = presence.Status,
                Activity = presence.Activity,
                Activities = activities,
                ClientStatus = presence.ClientStatus,
                HeartbeatTimestamp = presence.HeartbeatTimestamp
            });

            updates.Add(new PresenceUpdate(guildId, presence.Status, activities));
        }

        var settings = await privacy.GetAsync(message.UserId);

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub, blocks, settings);
    }

    /// <summary>
    /// Carries a start time forward for an activity that is still the same activity.
    /// </summary>
    internal static IReadOnlyList<ActivityDto> MergeStartTimes(
        IReadOnlyList<ActivityDto>? previous, IReadOnlyList<ActivityDto>? incoming, long nowMs)
    {
        if (incoming is null || incoming.Count == 0) return [];

        var priorStarts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var activity in previous ?? [])
        {
            // First wins.
            if (activity.StartedAt is { } started) priorStarts.TryAdd(IdentityKey(activity), started);
        }

        return incoming.Select(activity => new ActivityDto
        {
            Type = activity.Type,
            Name = activity.Name,
            Details = activity.Details,
            State = activity.State,
            ApplicationId = activity.ApplicationId,
            StartedAt = priorStarts.TryGetValue(IdentityKey(activity), out var prior)
                ? prior
                : activity.StartedAt ?? nowMs,
            EndsAt = activity.EndsAt,
            Assets = activity.Assets,
            Party = activity.Party,
            Source = activity.Source,
        }).ToList();

        // Separated, not concatenated: names are arbitrary text, so ("Playing", "AB", null) and
        // ("PlayingA", "B", null) would otherwise collapse to the same key and one game could
        // inherit another's start time.
        static string IdentityKey(ActivityDto activity) =>
            $"{activity.Type}\u001f{activity.Name}\u001f{activity.ApplicationId}";
    }

    // Marks the member offline in Redis immediately on disconnect rather than waiting for the
    // presence hash/ZSET entry to expire (previously the only mechanism - see the ghost-presence
    // stress test), then opens the voice grace window below.
    public async Task Handle(UserDisconnected message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IDistributedCache cache, VoiceRoomStore rooms,
        IHubContext<EchoRealtimeHub> hub, BlockCache blocks)
    {
        var userId = message.UserId;

        var members = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.GuildId })
            .ToListAsync();

        // Offline carries no Hidden to leak, but a block still applies: presence events must not
        // flow between a blocked pair in either direction, and "they just went offline" is a
        // presence event like any other.
        var blockView = members.Count > 0 ? await blocks.GetAsync([userId]) : BlockView.Empty;

        foreach (var m in members)
        {
            var presence = await service.GetGuildPresenceAsync(m.GuildId);
            await service.RemovePresenceStateAsync(m.GuildId, m.Id);

            var recipients = blockView.Reachable(userId,
                presence.Select(p => p.UserId).Distinct(StringComparer.Ordinal));

            if (recipients.Count == 0) continue;

            await hub.Clients.Users(recipients).SendAsync("guild.PresenceChanged",
                new { UserId = userId, GuildId = m.GuildId, Status = nameof(OnlineStatus.Offline) });
        }

        var locationJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(userId));
        if (locationJson is null) return;

        var location = JsonSerializer.Deserialize<UserVoiceLocation>(locationJson);
        if (location is null) return;

        var roomKey = VoiceRoomKey.Channel(location.ChannelId);
        var room = await rooms.LoadAsync(roomKey);

        if (room is null)
        {
            // The channel blob is gone (expired, or already cleaned up).
            if (DisconnectEndsVoiceConnection(location.DeviceId, message.DeviceId))
            {
                await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId));
                await cache.RemoveAsync(VoiceReconciler.LivenessKey(userId));
            }
            return;
        }

        var participant = room.Find(userId);

        // Nothing of this user's voice presence belonged to the device that just dropped, so
        // nothing about it changed - most often the other half of a takeover, where the superseded
        // device's own disconnect arrives after the new one is already in the roster.
        if (participant is null || !DisconnectEndsVoiceConnection(participant.DeviceId, message.DeviceId))
            return;

        // Read-only, deliberately.
        await cache.SetStringAsync(
            VoiceReconciler.LivenessKey(userId),
            roomKey.ToString(),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = VoiceReconciler.DisconnectGraceTtl
            });
    }

    /// <summary>
    /// Cancels the grace window opened by a disconnect, as soon as the device that holds the voice
    /// connection is back.
    /// </summary>
    private static async Task RestoreVoiceLivenessAsync(
        UserConnected message, IDistributedCache cache, VoiceRoomStore rooms)
    {
        var locationJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(message.UserId));
        if (locationJson is null) return;

        var location = JsonSerializer.Deserialize<UserVoiceLocation>(locationJson);
        if (location is null) return;

        var roomKey = VoiceRoomKey.Channel(location.ChannelId);
        var participant = (await rooms.LoadAsync(roomKey))?.Find(message.UserId);
        if (participant is null) return;
        if (!DisconnectEndsVoiceConnection(participant.DeviceId, message.DeviceId)) return;

        await cache.SetStringAsync(
            VoiceReconciler.LivenessKey(message.UserId),
            roomKey.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = VoiceReconciler.LivenessTtl });
    }

    /// <summary>
    /// Whether a socket lifecycle event on <paramref name="disconnectingDeviceId"/> concerns the
    /// voice connection held by <paramref name="voiceDeviceId"/>.
    /// </summary>
    private static bool DisconnectEndsVoiceConnection(string? voiceDeviceId, string? disconnectingDeviceId) =>
        string.IsNullOrEmpty(voiceDeviceId)
        || string.IsNullOrEmpty(disconnectingDeviceId)
        || voiceDeviceId == disconnectingDeviceId;
}
