using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Guild.Application.Controllers;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Realtime;

public class GuildVoiceStateHandler
{
    private static readonly DistributedCacheEntryOptions ChannelCacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    /// <summary>Builds a VoiceStateForBots snapshot from a participant's current full state -
    /// matches how Discord's own VOICE_STATE_UPDATE always carries the complete state, not a
    /// per-field delta.</summary>
    private static VoiceStateForBots ToVoiceStateForBots(VoiceState state, bool selfVideo = false) => new()
    {
        GuildId = state.GuildId,
        UserId = state.UserId,
        ChannelId = state.ChannelId,
        SelfMute = state.IsSelfMuted,
        SelfDeaf = state.IsSelfDeafened,
        SelfStream = state.IsStreaming,
        SelfVideo = selfVideo,
        Mute = state.IsServerMuted,
        Deaf = state.IsServerDeafened,
    };

    public async Task Handle(GuildVoiceHeartbeatCommand message, IDistributedCache cache)
    {
        await cache.SetStringAsync(
            ChannelVoiceState.GetHeartbeatCacheKey(message.UserId),
            "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90) });
    }

    public async Task Handle(GuildVoiceMuteCommand message, LockedJsonCacheStore voiceStore, IHubContext<EchoRealtimeHub> hub, IMessageBus bus)
    {
        var userId = message.UserId;

        // Locked: was an unsynchronized read-modify-write racing every other write in this
        // file plus GuildVoiceController.Join/LeaveChannelAsync and GuildCloudflareController
        // for the same channelId -see GuildVoiceController.Join for the class of bug this
        // silently caused (one side's change getting last-writer-wins overwritten).
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(message.ChannelId), ChannelVoiceState.GetCacheKey(message.ChannelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == userId);
                if (me is not null) me.IsSelfMuted = message.IsMuted;
            }, ChannelCacheOptions);
        if (voiceState is null) return;

        // The mutation above silently no-ops for someone who is not in this channel, but the
        // broadcast did not - so any authenticated user with a voice channel id could spam
        // fabricated mute events at that channel's roster, including in guilds they are not in.
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null) return;

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.MuteChanged",
            new { userId, isMuted = message.IsMuted, channelId = message.ChannelId, serverForced = false });

        await bus.PublishAsync(ToVoiceStateForBots(me));
    }

    public async Task Handle(GuildVoiceDeafenCommand message, LockedJsonCacheStore voiceStore, IHubContext<EchoRealtimeHub> hub, IMessageBus bus)
    {
        var userId = message.UserId;

        // Locked -see GuildVoiceMuteCommand above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(message.ChannelId), ChannelVoiceState.GetCacheKey(message.ChannelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == userId);
                if (me is not null) me.IsSelfDeafened = message.IsDeafened;
            }, ChannelCacheOptions);
        if (voiceState is null) return;

        // Only an actual participant may emit this - see GuildVoiceMuteCommand above.
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null) return;

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.DeafenChanged",
            new { userId, isDeafened = message.IsDeafened, channelId = message.ChannelId, serverForced = false });

        await bus.PublishAsync(ToVoiceStateForBots(me));
    }

    public async Task Handle(GuildVoiceCameraCommand message, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub, IMessageBus bus)
    {
        var userId = message.UserId;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

        // Only an actual participant may emit this - see GuildVoiceMuteCommand above.
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null) return;

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.CameraChanged",
            new { userId, isCameraOn = message.IsCameraOn, channelId = message.ChannelId });

        // Camera on/off isn't persisted on VoiceState (pre-existing - it's just relayed), so this
        // is the one field ToVoiceStateForBots can't derive from stored state; pass it through directly.
        await bus.PublishAsync(ToVoiceStateForBots(me, selfVideo: message.IsCameraOn));
    }

    public async Task Handle(GuildVoiceScreenShareStartCommand message, LockedJsonCacheStore voiceStore,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService,
        GuildVoiceActivityStore activity, IMessageBus bus)
    {
        var userId = message.UserId;

        var canStream = await permissionService.CanUserPerformActionAsync(userId, message.ChannelId, Permissions.Stream);
        if (!canStream) return;

        // Locked -see GuildVoiceMuteCommand above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(message.ChannelId), ChannelVoiceState.GetCacheKey(message.ChannelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == userId);
                if (me is not null) me.IsStreaming = true;
            }, ChannelCacheOptions);
        if (voiceState is null) return;

        var meAfter = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.ScreenShareStarted",
            new { userId, shareId = message.ShareId, trackName = message.TrackName, channelId = message.ChannelId });

        // Mirrored into the guild index so the server list can say "someone is live here" from the
        // one key it already reads, rather than opening every channel to find out.
        await activity.SetStreamingAsync(voiceState.GuildId, message.ChannelId, userId, true);

        if (meAfter is not null) await bus.PublishAsync(ToVoiceStateForBots(meAfter));
    }

    public async Task Handle(GuildVoiceScreenShareStopCommand message, LockedJsonCacheStore voiceStore,
        IHubContext<EchoRealtimeHub> hub, GuildVoiceActivityStore activity, StreamViewerStore viewers, IMessageBus bus)
    {
        var userId = message.UserId;

        // Locked -see GuildVoiceMuteCommand above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(message.ChannelId), ChannelVoiceState.GetCacheKey(message.ChannelId),
            vs =>
            {
                var me = vs.Participants.FirstOrDefault(p => p.UserId == userId);
                if (me is not null) me.IsStreaming = false;
            }, ChannelCacheOptions);
        if (voiceState is null) return;

        // Only an actual participant may emit this - see GuildVoiceMuteCommand above.
        var meAfter = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (meAfter is null) return;

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.ScreenShareStopped",
            new { shareId = message.ShareId, channelId = message.ChannelId });

        // The audience of a share that stopped is not empty, it is undefined - drop the entry
        // outright so a later share reusing the id can't inherit it.
        await viewers.RemoveShareAsync(StreamViewerStore.ChannelScope(message.ChannelId), message.ShareId);

        // Tracks IsStreaming above, which this same handler clears unconditionally on a stop.
        await activity.SetStreamingAsync(voiceState.GuildId, message.ChannelId, userId, false);

        await bus.PublishAsync(ToVoiceStateForBots(meAfter));
    }

    public async Task Handle(GuildVoiceServerMuteCommand message, LockedJsonCacheStore voiceStore,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService, IMessageBus bus)
    {
        var canMute = await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.MuteMembers);
        if (!canMute) return;

        // Locked -see GuildVoiceMuteCommand above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(message.ChannelId), ChannelVoiceState.GetCacheKey(message.ChannelId),
            vs =>
            {
                var target = vs.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
                if (target is not null) target.IsServerMuted = message.IsMuted;
            }, ChannelCacheOptions);
        var targetAfter = voiceState?.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
        if (targetAfter is null) return;

        var allIds = voiceState!.Participants.Select(p => p.UserId).ToList();
        await hub.Clients.Users(allIds).SendAsync("guild.voice.MuteChanged",
            new { userId = message.TargetUserId, isMuted = message.IsMuted, channelId = message.ChannelId, serverForced = true });

        await bus.PublishAsync(ToVoiceStateForBots(targetAfter));
    }

    public async Task Handle(GuildVoiceServerDeafenCommand message, LockedJsonCacheStore voiceStore,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService, IMessageBus bus)
    {
        var canDeafen = await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.DeafenMembers);
        if (!canDeafen) return;

        // Locked -see GuildVoiceMuteCommand above.
        var voiceState = await voiceStore.UpdateAsync<ChannelVoiceState>(
            ChannelVoiceState.GetCacheKey(message.ChannelId), ChannelVoiceState.GetCacheKey(message.ChannelId),
            vs =>
            {
                var target = vs.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
                if (target is not null) target.IsServerDeafened = message.IsDeafened;
            }, ChannelCacheOptions);
        var targetAfter = voiceState?.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
        if (targetAfter is null) return;

        var allIds = voiceState!.Participants.Select(p => p.UserId).ToList();
        await hub.Clients.Users(allIds).SendAsync("guild.voice.DeafenChanged",
            new { userId = message.TargetUserId, isDeafened = message.IsDeafened, channelId = message.ChannelId, serverForced = true });

        await bus.PublishAsync(ToVoiceStateForBots(targetAfter));
    }

    public async Task Handle(GuildVoiceMoveUserCommand message, IDistributedCache cache,
        LockedJsonCacheStore voiceStore, IDistributedLockService locks,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService,
        GuildVoiceActivityStore activity, StreamViewerStore viewers,
        MicroserviceContext microserviceContext, IMessageBus bus)
    {
        var userId = message.UserId;
        var canMove = await permissionService.CanUserPerformActionAsync(userId, message.ChannelId, Permissions.MoveMembers);
        if (!canMove) return;

        // The destination has to be authorized too, and it was not.
        var canMoveIntoTarget = await permissionService.CanUserPerformActionAsync(userId, message.TargetChannelId, Permissions.MoveMembers);
        if (!canMoveIntoTarget) return;

        // And the person being moved must themselves be allowed into the destination.
        var targetCanConnect = await permissionService.CanUserPerformActionAsync(message.TargetUserId, message.TargetChannelId, Permissions.Connect);
        if (!targetCanConnect) return;

        var sourceKey = ChannelVoiceState.GetCacheKey(message.ChannelId);
        var targetKey = ChannelVoiceState.GetCacheKey(message.TargetChannelId);

        // This touches two channel keys at once -acquire both locks in a fixed
        // lexicographic order (not call order) so a concurrent move in the opposite
        // direction (X->Y here, Y->X there) can't deadlock against this one.
        var (firstKey, secondKey) = string.CompareOrdinal(sourceKey, targetKey) <= 0
            ? (sourceKey, targetKey)
            : (targetKey, sourceKey);

        ChannelVoiceState voiceState;
        ChannelVoiceState targetVoiceState;
        VoiceState movedState;

        await using (await locks.AcquireAsync(firstKey))
        await using (await locks.AcquireAsync(secondKey))
        {
            var loaded = await voiceStore.LoadAsync<ChannelVoiceState>(sourceKey);
            var target = loaded?.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
            if (loaded is null || target is null) return;
            voiceState = loaded;

            // Remove from current channel
            voiceState.Participants.Remove(target);
            await voiceStore.SaveAsync(sourceKey, voiceState, ChannelCacheOptions);

            // Add to target channel
            targetVoiceState = await voiceStore.LoadAsync<ChannelVoiceState>(targetKey)
                ?? new ChannelVoiceState { ChannelId = message.TargetChannelId, GuildId = voiceState.GuildId };

            movedState = new VoiceState
            {
                UserId = message.TargetUserId,
                ChannelId = message.TargetChannelId,
                GuildId = voiceState.GuildId,
                DeviceId = target.DeviceId,
                JoinedAt = DateTime.UtcNow
            };
            targetVoiceState.Participants.Add(movedState);

            await voiceStore.SaveAsync(targetKey, targetVoiceState, ChannelCacheOptions);
        }

        // One index write for what is one fact - a move.
        await activity.MoveParticipantAsync(
            voiceState.GuildId, message.ChannelId, message.TargetChannelId, message.TargetUserId);

        // Whatever they were watching was in the channel they just left.
        await viewers.RemoveViewerAsync(StreamViewerStore.ChannelScope(message.ChannelId), message.TargetUserId);

        var onlineUserIds = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == voiceState.GuildId)
            .Select(m => m.UserId)
            .ToListAsync();

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
            new { userId = message.TargetUserId, channelId = message.ChannelId, guildId = voiceState.GuildId });

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(message.TargetUserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = message.TargetChannelId, GuildId = voiceState.GuildId, DeviceId = movedState.DeviceId }),
            ChannelCacheOptions);

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserJoinedVoice",
            new { userId = message.TargetUserId, channelId = message.TargetChannelId, guildId = voiceState.GuildId });

        // Notify the moved user specifically
        await hub.Clients.User(message.TargetUserId).SendAsync("guild.voice.MovedToChannel",
            new { channelId = message.TargetChannelId, guildId = voiceState.GuildId, movedBy = message.UserId });

        // A single VOICE_STATE_UPDATE with the new channel_id is enough - Discord doesn't
        // represent a move as a separate leave+join pair, just one state with the new channel.
        await bus.PublishAsync(ToVoiceStateForBots(movedState));
    }
}
