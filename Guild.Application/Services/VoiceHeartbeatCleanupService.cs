using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;

using Guild.Application.Models;
using Guild.Contracts.Bus.Events;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using Wolverine;

namespace Guild.Application.Services;

public class VoiceHeartbeatCleanupService(
    IConnectionMultiplexer redis,
    IDistributedCache cache,
    VoiceRoomStore rooms,
    VoiceAnnouncer announcer,
    GuildVoiceActivityStore activityStore,
    StreamViewerStore viewers,
    IHubContext<EchoRealtimeHub> hub,
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

        // Both room kinds, not just channels.
        var keys = server.Keys(pattern: "voice:room:*")
            .Select(k => k.ToString()["voice:room:".Length..])
            .Select(k => k.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new VoiceRoomKey(parts[0], parts[1]))
            .ToList();

        // Accumulated as the sweep goes, then written back per guild.
        var rebuilt = new Dictionary<string, Dictionary<string, ChannelVoiceActivity>>();

        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) break;

            var roomKey = key;
            var isChannel = roomKey.Kind == VoiceRoomKind.Channel;
            var loaded = await rooms.LoadAsync(roomKey, ct);
            if (loaded is null) continue;

            var stale = new List<VoiceParticipant>();
            foreach (var participant in loaded.Participants)
            {
                var heartbeat = await cache.GetStringAsync(
                    VoiceReconciler.LivenessKey(participant.UserId), ct);
                if (heartbeat is null)
                    stale.Add(participant);
            }

            if (stale.Count == 0)
            {
                if (isChannel) Record(rebuilt, loaded);
                continue;
            }

            var staleIds = stale.Select(p => p.UserId).ToHashSet();
            // Locked: this background sweep racing a live Join/mute/etc. write for the same channel
            // was another instance of the same read-modify-write class of bug -see
            // GuildVoiceController.Join.
            var voiceState = await rooms.MutateExistingAsync(
                roomKey,
                r => r.Participants.RemoveAll(p => staleIds.Contains(p.UserId)), ct);
            if (voiceState is null) continue;

            if (isChannel) Record(rebuilt, voiceState);

            // An evicted participant's watch claims and their own shares both die with them.
            var viewerScope = roomKey.ViewerScope;
            foreach (var participant in stale)
            {
                await viewers.RemoveViewerAsync(viewerScope, participant.UserId, ct);
                var owned = participant.ActiveScreenShares.Select(s => s.ShareId).ToList();
                if (owned.Count > 0) await viewers.RemoveSharesAsync(viewerScope, owned, ct);
            }

            // Tell the room itself, for both kinds: an evicted participant is a roster change, so
            // peers have to be able to detect it like any other.
            foreach (var participant in stale)
            {
                logger.LogInformation(
                    "Evicted stale voice participant {UserId} from room {Room}",
                    participant.UserId, roomKey);
            }
            await announcer.ToAllAsync(voiceState, VoiceEvents.Resync,
                new { reason = "participantsEvicted" }, ct);

            // Everything below is the guild-only fan-out: a channel is visible to members who are
            // not in it, which a call has no equivalent of.
            if (!isChannel) continue;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            var memberIds = await db.GuildMembers
                .AsNoTracking()
                .Where(m => m.GuildId == voiceState.GuildId)
                .Select(m => m.UserId)
                .ToListAsync(ct);

            foreach (var participant in stale)
            {
                await hub.Clients.Users(memberIds).SendAsync("guild.voice.UserLeftVoice",
                    new { userId = participant.UserId, channelId = roomKey.Id, guildId = voiceState.GuildId },
                    ct);

                await bus.PublishAsync(new VoiceStateForBots { GuildId = voiceState.GuildId, UserId = participant.UserId, ChannelId = null });
            }
        }

        foreach (var (guildId, channels) in rebuilt)
        {
            await activityStore.ReplaceAsync(guildId, channels, ct);
        }
    }

    /// <summary>Folds one channel's authoritative roster into the guild index being rebuilt.</summary>
    internal static void Record(
        Dictionary<string, Dictionary<string, ChannelVoiceActivity>> rebuilt,
        VoiceRoom state)
    {
        if (string.IsNullOrWhiteSpace(state.GuildId)) return;

        if (!rebuilt.TryGetValue(state.GuildId, out var channels))
        {
            channels = new Dictionary<string, ChannelVoiceActivity>();
            rebuilt[state.GuildId] = channels;
        }

        // Recorded even when empty: a guild whose last participant just left still has to be
        // written back, or the index would keep reporting the roster it had before.
        channels[state.RoomId] = new ChannelVoiceActivity
        {
            UserIds = state.Participants.Select(p => p.UserId).ToList(),
            StreamerIds = state.Participants.Where(p => p.IsStreaming).Select(p => p.UserId).ToList(),
        };
    }
}
