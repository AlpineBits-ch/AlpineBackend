using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Guild.Application.Controllers;
using Guild.Application.Dtos.Response;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Events;
using Wolverine;

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

    // A brand-new connection defaults to Online (matches Discord's default) — there is no prior
    // presence entry to preserve a status from.
    public async Task Handle(UserConnected message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks)
    {
        var updates = await RefreshPresenceAsync(message.UserId, microserviceContext, service,
            defaultStatus: nameof(OnlineStatus.Online));

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub, blocks);
    }

    // The gateway hub republishes this while the connection is alive (throttled), replacing the old
    // IConnectionHeartbeatFeature per-pulse refresh.
    public Task Handle(PresenceHeartbeat message, MicroserviceContext microserviceContext, GuildHydrateService service)
        => RefreshPresenceAsync(message.UserId, microserviceContext, service, defaultStatus: null);

    private static async Task<List<(string GuildId, string Status)>> RefreshPresenceAsync(
        string userId, MicroserviceContext ctx, GuildHydrateService service, string? defaultStatus)
    {
        var members = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .ToListAsync();

        var updates = new List<(string GuildId, string Status)>();

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
                ClientStatus = existing?.ClientStatus,
                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            updates.Add((m.GuildId, status));
        }

        return updates;
    }

    /// <summary>Fans a presence change out to the guilds the user is in.</summary>
    private static async Task BroadcastPresenceChangesAsync(string userId, List<(string GuildId, string Status)> updates,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks)
    {
        if (updates.Count == 0) return;

        var blockView = await blocks.GetAsync([userId]);

        foreach (var (guildId, status) in updates)
        {
            var presence = await service.GetGuildPresenceAsync(guildId);
            var recipients = presence.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();

            var self = recipients.Where(id => string.Equals(id, userId, StringComparison.Ordinal)).ToList();
            var others = blockView.Reachable(userId,
                recipients.Where(id => !string.Equals(id, userId, StringComparison.Ordinal)));

            if (self.Count > 0)
            {
                await hub.Clients.Users(self).SendAsync("guild.PresenceChanged",
                    new { UserId = userId, GuildId = guildId, Status = status });
            }

            if (others.Count > 0)
            {
                await hub.Clients.Users(others).SendAsync("guild.PresenceChanged",
                    new
                    {
                        UserId = userId,
                        GuildId = guildId,
                        Status = PresenceProjection.ProjectNameFor(status, viewerIsSubject: false),
                    });
            }
        }
    }

    public async Task Handle(UserStatusChanged message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub, BlockCache blocks)
    {
        var members = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == message.UserId)
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .ToListAsync();

        var updates = new List<(string GuildId, string Status)>();

        foreach (var m in members)
        {
            var existing = await service.GetPresenceStateForMemberAsync(m.Id);

            await service.AddPresenceStateAsync(m.GuildId, new MemberPresenceState
            {
                MemberId = m.Id,
                UserId = m.UserId,
                Status = message.Status,
                Activity = existing?.Activity,
                ClientStatus = existing?.ClientStatus,
                HeartbeatTimestamp = existing?.HeartbeatTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            updates.Add((m.GuildId, message.Status));
        }

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub, blocks);
    }

    // Marks the member offline in Redis immediately on disconnect rather than waiting for the
    // presence hash/ZSET entry to expire (previously the only mechanism — see the ghost-presence
    // stress test), then falls through to the pre-existing voice-cleanup logic below.
    public async Task Handle(UserDisconnected message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IDistributedCache cache, LockedJsonCacheStore voiceStore,
        IHubContext<EchoRealtimeHub> hub, IMessageBus bus, BlockCache blocks)
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

        var channelKey = ChannelVoiceState.GetCacheKey(location.ChannelId);

        // Locked: this shares the channel blob with Join, CreateSession, ExchangeParticipantJoined,
        // CloseTracks, the mute/deafen/screenshare handlers and the heartbeat sweeper — every one
        // of which was moved onto LockedJsonCacheStore for this reason and this one call site was
        // missed.
        var removedFromVoice = false;
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(channelKey, channelKey,
            vs =>
            {
                var participant = vs.Participants.FirstOrDefault(p => p.UserId == userId);
                if (participant is null) return;
                if (!DisconnectEndsVoiceConnection(participant.DeviceId, message.DeviceId)) return;

                vs.Participants.Remove(participant);
                removedFromVoice = true;
            }, ChannelCacheOptions);

        if (voiceState is null)
        {
            // The channel blob is gone (expired, or already cleaned up).
            if (DisconnectEndsVoiceConnection(location.DeviceId, message.DeviceId))
            {
                await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId));
                await cache.RemoveAsync(ChannelVoiceState.GetHeartbeatCacheKey(userId));
            }
            return;
        }

        // Nothing of this user's voice presence belonged to the device that just dropped, so
        // nothing about it changed.
        if (!removedFromVoice) return;

        var onlineUserIds = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == location.GuildId)
            .Select(m => m.UserId)
            .ToListAsync();

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
            new { userId, channelId = location.ChannelId, guildId = location.GuildId });

        await bus.PublishAsync(new VoiceStateForBots { GuildId = location.GuildId, UserId = userId, ChannelId = null });

        await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId));
        await cache.RemoveAsync(ChannelVoiceState.GetHeartbeatCacheKey(userId));
    }

    /// <summary>
    /// Whether a socket drop on <paramref name="disconnectingDeviceId"/> should be treated as
    /// ending the voice connection held by <paramref name="voiceDeviceId"/>.
    /// </summary>
    private static bool DisconnectEndsVoiceConnection(string? voiceDeviceId, string? disconnectingDeviceId) =>
        string.IsNullOrEmpty(voiceDeviceId)
        || string.IsNullOrEmpty(disconnectingDeviceId)
        || voiceDeviceId == disconnectingDeviceId;
}
