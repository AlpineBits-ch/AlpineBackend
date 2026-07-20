using System.Text.Json;
using Echo.Realtime;
using Guild.Application.Controllers;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Bus.Events.Realtime;

public class GuildVoiceStateHandler
{
    private static readonly DistributedCacheEntryOptions ChannelCacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(4)
    };

    public async Task Handle(GuildVoiceHeartbeatCommand message, IDistributedCache cache)
    {
        await cache.SetStringAsync(
            ChannelVoiceState.GetHeartbeatCacheKey(message.UserId),
            "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90) });
    }

    public async Task Handle(GuildVoiceMuteCommand message, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var userId = message.UserId;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsSelfMuted = message.IsMuted;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
                JsonSerializer.Serialize(voiceState), ChannelCacheOptions);
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.MuteChanged",
            new { userId, isMuted = message.IsMuted, channelId = message.ChannelId, serverForced = false });
    }

    public async Task Handle(GuildVoiceDeafenCommand message, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var userId = message.UserId;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsSelfDeafened = message.IsDeafened;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
                JsonSerializer.Serialize(voiceState), ChannelCacheOptions);
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.DeafenChanged",
            new { userId, isDeafened = message.IsDeafened, channelId = message.ChannelId, serverForced = false });
    }

    public async Task Handle(GuildVoiceCameraCommand message, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var userId = message.UserId;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.CameraChanged",
            new { userId, isCameraOn = message.IsCameraOn, channelId = message.ChannelId });
    }

    public async Task Handle(GuildVoiceScreenShareStartCommand message, IDistributedCache cache,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService)
    {
        var userId = message.UserId;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

        var canStream = await permissionService.CanUserPerformActionAsync(userId, message.ChannelId, Permissions.Stream);
        if (!canStream) return;

        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsStreaming = true;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
                JsonSerializer.Serialize(voiceState), ChannelCacheOptions);
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.ScreenShareStarted",
            new { userId, shareId = message.ShareId, trackName = message.TrackName, channelId = message.ChannelId });
    }

    public async Task Handle(GuildVoiceScreenShareStopCommand message, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var userId = message.UserId;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsStreaming = false;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
                JsonSerializer.Serialize(voiceState), ChannelCacheOptions);
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await hub.Clients.Users(otherIds).SendAsync("guild.voice.ScreenShareStopped",
            new { shareId = message.ShareId, channelId = message.ChannelId });
    }

    public async Task Handle(GuildVoiceServerMuteCommand message, IDistributedCache cache,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService)
    {
        var canMute = await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.MuteMembers);
        if (!canMute) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var target = voiceState.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
        if (target is null) return;

        target.IsServerMuted = message.IsMuted;
        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
            JsonSerializer.Serialize(voiceState), ChannelCacheOptions);

        var allIds = voiceState.Participants.Select(p => p.UserId).ToList();
        await hub.Clients.Users(allIds).SendAsync("guild.voice.MuteChanged",
            new { userId = message.TargetUserId, isMuted = message.IsMuted, channelId = message.ChannelId, serverForced = true });
    }

    public async Task Handle(GuildVoiceServerDeafenCommand message, IDistributedCache cache,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService)
    {
        var canDeafen = await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.DeafenMembers);
        if (!canDeafen) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var target = voiceState.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
        if (target is null) return;

        target.IsServerDeafened = message.IsDeafened;
        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
            JsonSerializer.Serialize(voiceState), ChannelCacheOptions);

        var allIds = voiceState.Participants.Select(p => p.UserId).ToList();
        await hub.Clients.Users(allIds).SendAsync("guild.voice.DeafenChanged",
            new { userId = message.TargetUserId, isDeafened = message.IsDeafened, channelId = message.ChannelId, serverForced = true });
    }

    public async Task Handle(GuildVoiceMoveUserCommand message, IDistributedCache cache,
        IHubContext<EchoRealtimeHub> hub, GuildPermissionService permissionService,
        MicroserviceContext microserviceContext)
    {
        var userId = message.UserId;
        var canMove = await permissionService.CanUserPerformActionAsync(userId, message.ChannelId, Permissions.MoveMembers);
        if (!canMove) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var target = voiceState.Participants.FirstOrDefault(p => p.UserId == message.TargetUserId);
        if (target is null) return;

        // Remove from current channel
        voiceState.Participants.Remove(target);
        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.ChannelId),
            JsonSerializer.Serialize(voiceState), ChannelCacheOptions);

        var onlineUserIds = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == voiceState.GuildId)
            .Select(m => m.UserId)
            .ToListAsync();

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserLeftVoice",
            new { userId = message.TargetUserId, channelId = message.ChannelId, guildId = voiceState.GuildId });

        // Add to target channel
        var targetRaw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(message.TargetChannelId));
        var targetVoiceState = targetRaw is not null
            ? JsonSerializer.Deserialize<ChannelVoiceState>(targetRaw)!
            : new ChannelVoiceState { ChannelId = message.TargetChannelId, GuildId = voiceState.GuildId };

        targetVoiceState.Participants.Add(new VoiceState
        {
            UserId = message.TargetUserId,
            ChannelId = message.TargetChannelId,
            GuildId = voiceState.GuildId,
            JoinedAt = DateTime.UtcNow
        });

        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(message.TargetChannelId),
            JsonSerializer.Serialize(targetVoiceState), ChannelCacheOptions);

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(message.TargetUserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = message.TargetChannelId, GuildId = voiceState.GuildId }),
            ChannelCacheOptions);

        await hub.Clients.Users(onlineUserIds).SendAsync("guild.voice.UserJoinedVoice",
            new { userId = message.TargetUserId, channelId = message.TargetChannelId, guildId = voiceState.GuildId });

        // Notify the moved user specifically
        await hub.Clients.User(message.TargetUserId).SendAsync("guild.voice.MovedToChannel",
            new { channelId = message.TargetChannelId, guildId = voiceState.GuildId, movedBy = message.UserId });
    }
}
