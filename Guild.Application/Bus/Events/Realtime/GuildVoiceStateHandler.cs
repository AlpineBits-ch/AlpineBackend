using System.Text.Json;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
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

/// <summary>Guild voice state changes arriving over the hub.</summary>
public class GuildVoiceStateHandler
{
    private static VoiceRoomKey Room(string channelId) => VoiceRoomKey.Channel(channelId);

    /// <summary>Builds a VoiceStateForBots snapshot from a participant's current full state -
    /// matches how Discord's own VOICE_STATE_UPDATE always carries the complete state, not a
    /// per-field delta.</summary>
    private static VoiceStateForBots ToVoiceStateForBots(
        VoiceRoom room, VoiceParticipant p, bool selfVideo = false) => new()
    {
        GuildId = room.GuildId ?? string.Empty,
        UserId = p.UserId,
        ChannelId = room.RoomId,
        SelfMute = p.IsSelfMuted,
        SelfDeaf = p.IsSelfDeafened,
        SelfStream = p.IsStreaming,
        SelfVideo = selfVideo,
        Mute = p.IsServerMuted,
        Deaf = p.IsServerDeafened,
    };


    /// <summary>The state-asserting heartbeat.</summary>
    public Task Handle(GuildVoiceReconcileCommand message, VoiceReconciler reconciler) =>
        reconciler.HeartbeatAsync(
            message.UserId, Room(message.ChannelId),
            message.State.KnownInstanceId, message.State.KnownVersion,
            message.State.MediaSessionId, message.State.AudioTrackName);

    public async Task Handle(GuildVoiceMuteCommand message, VoiceRoomService voice, IMessageBus bus)
    {
        var room = await voice.SetMuteAsync(Room(message.ChannelId), message.UserId, message.IsMuted, serverForced: false);
        if (room?.Find(message.UserId) is { } me)
            await bus.PublishAsync(ToVoiceStateForBots(room, me));
    }

    public async Task Handle(GuildVoiceDeafenCommand message, VoiceRoomService voice, IMessageBus bus)
    {
        var room = await voice.SetDeafenAsync(Room(message.ChannelId), message.UserId, message.IsDeafened, serverForced: false);
        if (room?.Find(message.UserId) is { } me)
            await bus.PublishAsync(ToVoiceStateForBots(room, me));
    }

    public async Task Handle(GuildVoiceCameraCommand message, VoiceRoomService voice, IMessageBus bus)
    {
        var room = await voice.SetCameraAsync(Room(message.ChannelId), message.UserId, message.IsCameraOn);
        if (room?.Find(message.UserId) is { } me)
            // Camera state isn't persisted on the roster - it's relayed - so this is the one field
            // that has to be passed through rather than derived.
            await bus.PublishAsync(ToVoiceStateForBots(room, me, selfVideo: message.IsCameraOn));
    }

    /// <summary>Speech, relayed to peers and fed to active-speaker ranking.</summary>
    public Task Handle(GuildVoiceSpeakingCommand message, VoiceRoomService voice) =>
        voice.SetSpeakingAsync(Room(message.ChannelId), message.UserId, message.IsSpeaking);

    public async Task Handle(GuildVoiceScreenShareStartCommand message, VoiceRoomService voice,
        GuildPermissionService permissionService, GuildVoiceActivityStore activity, IMessageBus bus)
    {
        if (!await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.Stream))
            return;

        var room = await voice.SetStreamingAsync(
            Room(message.ChannelId), message.UserId, isStreaming: true, message.ShareId);
        if (room?.Find(message.UserId) is not { } me) return;

        // Mirrored into the guild index so the server list can say "someone is live here" from the
        // one key it already reads.
        await activity.SetStreamingAsync(room.GuildId ?? string.Empty, message.ChannelId, message.UserId, true);

        await bus.PublishAsync(ToVoiceStateForBots(room, me));
    }

    public async Task Handle(GuildVoiceScreenShareStopCommand message, VoiceRoomService voice,
        GuildVoiceActivityStore activity, StreamViewerStore viewers, IMessageBus bus)
    {
        var room = await voice.SetStreamingAsync(
            Room(message.ChannelId), message.UserId, isStreaming: false, message.ShareId);
        if (room?.Find(message.UserId) is not { } me) return;

        // The audience of a share that stopped is not empty, it is undefined - drop the entry
        // outright so a later share reusing the id can't inherit it.
        await viewers.RemoveShareAsync(Room(message.ChannelId).ViewerScope, message.ShareId);

        await activity.SetStreamingAsync(room.GuildId ?? string.Empty, message.ChannelId, message.UserId, false);

        await bus.PublishAsync(ToVoiceStateForBots(room, me));
    }

    public async Task Handle(GuildVoiceServerMuteCommand message, VoiceRoomService voice,
        GuildPermissionService permissionService, IMessageBus bus)
    {
        if (!await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.MuteMembers))
            return;

        var room = await voice.SetMuteAsync(
            Room(message.ChannelId), message.TargetUserId, message.IsMuted, serverForced: true);
        if (room?.Find(message.TargetUserId) is { } target)
            await bus.PublishAsync(ToVoiceStateForBots(room, target));
    }

    public async Task Handle(GuildVoiceServerDeafenCommand message, VoiceRoomService voice,
        GuildPermissionService permissionService, IMessageBus bus)
    {
        if (!await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.DeafenMembers))
            return;

        var room = await voice.SetDeafenAsync(
            Room(message.ChannelId), message.TargetUserId, message.IsDeafened, serverForced: true);
        if (room?.Find(message.TargetUserId) is { } target)
            await bus.PublishAsync(ToVoiceStateForBots(room, target));
    }

    /// <summary>Moving a member between two voice channels.</summary>
    public async Task Handle(GuildVoiceMoveUserCommand message, IDistributedCache cache,
        VoiceRoomService voice, VoiceRoomStore rooms,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService,
        GuildVoiceActivityStore activity, StreamViewerStore viewers,
        MicroserviceContext microserviceContext, IMessageBus bus)
    {
        var userId = message.UserId;
        if (!await permissionService.CanUserPerformActionAsync(userId, message.ChannelId, Permissions.MoveMembers))
            return;

        // The destination has to be authorized too: MoveMembers on the source alone would let a
        // moderator move themselves into a channel that denies them Connect, and from inside the
        // roster the SFU track exchange then hands them the other participants' sessions.
        if (!await permissionService.CanUserPerformActionAsync(userId, message.TargetChannelId, Permissions.MoveMembers))
            return;

        // And the person being moved must themselves be allowed into the destination.
        if (!await permissionService.CanUserPerformActionAsync(message.TargetUserId, message.TargetChannelId, Permissions.Connect))
            return;

        var source = await rooms.LoadAsync(Room(message.ChannelId));
        if (source?.Find(message.TargetUserId) is not { } moving) return;
        var guildId = source.GuildId ?? string.Empty;

        // Two rooms, two locks, so this is expressed as a leave and a join rather than one
        // critical section - each is individually locked and individually versioned, and the
        // ordering below never leaves the participant in both rooms at once.
        await voice.LeaveAsync(Room(message.ChannelId), message.TargetUserId);
        var target = await voice.JoinAsync(
            Room(message.TargetChannelId), message.TargetUserId, moving.DeviceId, guildId);

        // One index write for what is one fact - a move.
        await activity.MoveParticipantAsync(
            guildId, message.ChannelId, message.TargetChannelId, message.TargetUserId);

        // Whatever they were watching was in the channel they just left.
        await viewers.RemoveViewerAsync(Room(message.ChannelId).ViewerScope, message.TargetUserId);

        var onlineUserIds = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => m.UserId)
            .ToListAsync();

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
            new { userId = message.TargetUserId, channelId = message.ChannelId, guildId });

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(message.TargetUserId),
            JsonSerializer.Serialize(new UserVoiceLocation
            {
                ChannelId = message.TargetChannelId, GuildId = guildId, DeviceId = moving.DeviceId
            }),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserJoinedVoice",
            new { userId = message.TargetUserId, channelId = message.TargetChannelId, guildId });

        await hub.Clients.User(message.TargetUserId).SendAsync("guild.voice.MovedToChannel",
            new { channelId = message.TargetChannelId, guildId, movedBy = message.UserId });

        // A single VOICE_STATE_UPDATE with the new channel_id is enough - Discord doesn't
        // represent a move as a separate leave+join pair.
        if (target.Find(message.TargetUserId) is { } moved)
            await bus.PublishAsync(ToVoiceStateForBots(target, moved));
    }
}
