using System.Text.Json;
using Guild.Application.Hubs;
using Guild.Application.Models;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Guild.Application.Services;

public class VoiceHeartbeatCleanupService(
    IConnectionMultiplexer redis,
    IDistributedCache cache,
    IHubContext<GuildHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<VoiceHeartbeatCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly DistributedCacheEntryOptions ChannelCacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await EvictStaleParticipantsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Voice heartbeat cleanup failed");
            }
        }
    }

    private async Task EvictStaleParticipantsAsync(CancellationToken ct)
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        var keys = server.Keys(pattern: "voice:channel:*");

        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) break;

            var channelId = key.ToString()["voice:channel:".Length..];
            var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(channelId), ct);
            if (raw is null) continue;

            var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

            var stale = new List<VoiceState>();
            foreach (var participant in voiceState.Participants)
            {
                var heartbeat = await cache.GetStringAsync(
                    ChannelVoiceState.GetHeartbeatCacheKey(participant.UserId), ct);
                if (heartbeat is null)
                    stale.Add(participant);
            }

            if (stale.Count == 0) continue;

            foreach (var participant in stale)
                voiceState.Participants.Remove(participant);

            await cache.SetStringAsync(
                ChannelVoiceState.GetCacheKey(channelId),
                JsonSerializer.Serialize(voiceState),
                ChannelCacheOptions,
                ct);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var memberIds = await db.GuildMembers
                .AsNoTracking()
                .Where(m => m.GuildId == voiceState.GuildId)
                .Select(m => m.UserId)
                .ToListAsync(ct);

            foreach (var participant in stale)
            {
                logger.LogInformation(
                    "Evicted stale voice participant {UserId} from channel {ChannelId}",
                    participant.UserId, channelId);

                await hub.Clients.Users(memberIds).SendAsync("UserLeftVoice",
                    new { userId = participant.UserId, channelId, guildId = voiceState.GuildId },
                    ct);
            }
        }
    }
}
