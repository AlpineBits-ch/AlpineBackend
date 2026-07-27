using System.Text.Json;
using Echo.Realtime;
using Guild.Application.Controllers;
using Guild.Application.Dtos.Response;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Events;

namespace Guild.Application.Bus.Events.Realtime;

/// <summary>
/// Presence + voice cleanup that used to live in GuildHub.OnConnectedAsync / OnDisconnectedAsync
/// and the per-pulse heartbeat callback. Driven now by the gateway hub's lifecycle commands.
/// </summary>
public class GuildLifecycleHandler
{
    // A brand-new connection defaults to Online (matches Discord's default) — there is no
    // prior presence entry to preserve a status from. Guild members watching this user's guilds
    // are notified in realtime, matching the notification already sent for explicit status changes.
    public async Task Handle(UserConnected message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub)
    {
        var updates = await RefreshPresenceAsync(message.UserId, microserviceContext, service,
            defaultStatus: nameof(OnlineStatus.Online));

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub);
    }

    // The gateway hub republishes this while the connection is alive (throttled), replacing the
    // old IConnectionHeartbeatFeature per-pulse refresh. Previously this also hardcoded Online,
    // which silently clobbered any status the user had explicitly set (Idle/DoNotDisturb/Hidden)
    // back to Online on the next ~30s tick — preserve whatever is already cached instead.
    // No broadcast here: status is (almost always) unchanged between heartbeats, so notifying
    // every ~30s would just spam guild members without new information.
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

    private static async Task BroadcastPresenceChangesAsync(string userId, List<(string GuildId, string Status)> updates,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub)
    {
        foreach (var (guildId, status) in updates)
        {
            var presence = await service.GetGuildPresenceAsync(guildId);
            await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.PresenceChanged",
                new { UserId = userId, GuildId = guildId, Status = status });
        }
    }

    public async Task Handle(UserStatusChanged message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IHubContext<EchoRealtimeHub> hub)
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

        await BroadcastPresenceChangesAsync(message.UserId, updates, service, hub);
    }

    // Marks the member offline in Redis immediately on disconnect rather than waiting for the
    // presence hash/ZSET entry to expire (previously the only mechanism — see the ghost-presence
    // stress test), then falls through to the pre-existing voice-cleanup logic below.
    public async Task Handle(UserDisconnected message, MicroserviceContext microserviceContext,
        GuildHydrateService service, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var userId = message.UserId;

        var members = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.GuildId })
            .ToListAsync();

        foreach (var m in members)
        {
            var presence = await service.GetGuildPresenceAsync(m.GuildId);
            await service.RemovePresenceStateAsync(m.GuildId, m.Id);

            await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.PresenceChanged",
                new { UserId = userId, GuildId = m.GuildId, Status = nameof(OnlineStatus.Offline) });
        }

        var locationJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(userId));
        if (locationJson is null) return;

        var location = JsonSerializer.Deserialize<UserVoiceLocation>(locationJson);
        if (location is null) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(location.ChannelId));
        if (raw is not null)
        {
            var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
            var participant = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant is not null)
            {
                voiceState.Participants.Remove(participant);
                await cache.SetStringAsync(
                    ChannelVoiceState.GetCacheKey(location.ChannelId),
                    JsonSerializer.Serialize(voiceState),
                    new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

                var onlineUserIds = await microserviceContext.GuildMembers
                    .AsNoTracking()
                    .Where(m => m.GuildId == location.GuildId)
                    .Select(m => m.UserId)
                    .ToListAsync();

                await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
                    new { userId, channelId = location.ChannelId, guildId = location.GuildId });
            }
        }

        await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId));
        await cache.RemoveAsync(ChannelVoiceState.GetHeartbeatCacheKey(userId));
    }
}
