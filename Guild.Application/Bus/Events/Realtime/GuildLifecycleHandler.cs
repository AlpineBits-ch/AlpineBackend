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

namespace Guild.Application.Bus.Events.Realtime;

/// <summary>
/// Presence + voice cleanup that used to live in GuildHub.OnConnectedAsync / OnDisconnectedAsync
/// and the per-pulse heartbeat callback. Driven now by the gateway hub's lifecycle commands.
/// </summary>
public class GuildLifecycleHandler
{
    public Task Handle(UserConnected message, MicroserviceContext microserviceContext, GuildHydrateService service)
        => RefreshPresenceAsync(message.UserId, microserviceContext, service);

    // The gateway hub republishes this while the connection is alive (throttled), replacing the
    // old IConnectionHeartbeatFeature per-pulse refresh.
    public Task Handle(PresenceHeartbeat message, MicroserviceContext microserviceContext, GuildHydrateService service)
        => RefreshPresenceAsync(message.UserId, microserviceContext, service);

    private static async Task RefreshPresenceAsync(string userId, MicroserviceContext ctx, GuildHydrateService service)
    {
        var members = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.UserId, m.GuildId })
            .ToListAsync();

        foreach (var m in members)
        {
            await service.AddPresenceStateAsync(m.GuildId, new MemberPresenceState
            {
                MemberId = m.Id,
                UserId = m.UserId,
                Status = nameof(OnlineStatus.Online),
                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }
    }

    public async Task Handle(UserDisconnected message, MicroserviceContext microserviceContext,
        IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var userId = message.UserId;

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
