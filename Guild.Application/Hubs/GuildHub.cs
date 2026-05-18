using System.Text.Json;
using Guild.Application.Controllers;
using Guild.Application.Dtos.Response;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Hubs;

internal record MemberWithUserId
{
    public string UserId { get; init; }
    public string MemberId { get; init; }

    public string GuildId { get; init; }
}

public class UpdateLastReadMessageByChannelDto
{
    public string Id { get; init; }
    public string ChannelId { get; init; }
}

public class UserTypingEvent
{
    public string UserId { get; init; }
    public string ChannelId { get; init; }
}

public record VoiceMuteChangedDto(string ChannelId, bool IsMuted);
public record VoiceDeafenChangedDto(string ChannelId, bool IsDeafened);
public record VoiceCameraChangedDto(string ChannelId, bool IsCameraOn);
public record VoiceScreenShareStartedDto(string ChannelId, string ShareId, string TrackName);
public record VoiceScreenShareStoppedDto(string ChannelId, string ShareId);
public record VoiceServerMuteDto(string ChannelId, string TargetUserId, bool IsMuted);
public record VoiceServerDeafenDto(string ChannelId, string TargetUserId, bool IsDeafened);
public record VoiceMoveUserDto(string ChannelId, string TargetUserId, string TargetChannelId);

public class GuildHub(
    ILogger<GuildHub> logger,
    MicroserviceContext microserviceContext,
    GuildHydrateService service,
    GuildPermissionService permissionService,
    IDistributedCache cache,
    IHostApplicationLifetime lifetime) : Hub
{
    private static async Task<ICollection<MemberWithUserId>> GetMemberIdsFromUserIdAsync(string userId, MicroserviceContext microserviceContext)
    {
        return await microserviceContext.GuildMembers.Where(m => m.UserId == userId).Select(m => new MemberWithUserId
            {
                UserId = m.UserId,
                MemberId = m.Id,
                GuildId = m.GuildId
            })
            .ToListAsync();
    }

    public override async Task OnConnectedAsync()
    {
        if (string.IsNullOrWhiteSpace(Context.UserIdentifier)) throw new Exception("User not authenticated");
        logger.LogInformation("Client connected to voice hub, id {ConnectionId}, userId {UserId}", Context.ConnectionId,
            Context.UserIdentifier);

        var memberData = await GetMemberIdsFromUserIdAsync(Context.UserIdentifier, microserviceContext);
        foreach (var memberWithUserId in memberData)
            await service.AddPresenceStateAsync(memberWithUserId.GuildId, new MemberPresenceState
            {
                MemberId = memberWithUserId.MemberId,
                UserId = memberWithUserId.UserId,
                Status = nameof(OnlineStatus.Online),
                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        
        var heartbeat = Context.Features.Get<IConnectionHeartbeatFeature>();
        if (heartbeat is not null)
        {
            // Capture the root provider, not the request-scoped one
            var rootProvider = Context.GetHttpContext()?.RequestServices;
            var userId = Context.UserIdentifier;

            heartbeat.OnHeartbeat(state =>
            {
                // Use Task.Run to offload the async work so we don't block the heartbeat thread
                _ = Task.Run(async () => 
                {
                    try
                    {
                        // Create a NEW scope for every pulse to ensure DB contexts are fresh and thread-safe
                        using var scope = rootProvider.CreateScope();
                        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                        var hydrateService = scope.ServiceProvider.GetRequiredService<GuildHydrateService>();

                        var memberIds = await GetMemberIdsFromUserIdAsync((string)state, ctx);

                        foreach (var memberWithUserId in memberIds)
                        {
                            await hydrateService.AddPresenceStateAsync(memberWithUserId.GuildId, new MemberPresenceState
                            {
                                MemberId = memberWithUserId.MemberId,
                                UserId = memberWithUserId.UserId,
                                Status = nameof(OnlineStatus.Online),
                                HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        // Log thoroughly - async void/background tasks are silent killers
                        logger.LogError(e, "Error processing heartbeat for {UserId}", state);
                    }
                });
            }, userId);
        }
        else
        {
            logger.LogWarning("Connection heartbeat feature not found");
        }
        
        await base.OnConnectedAsync();
    }

    [HubMethodName("UpdateLastReadMessageByChannel")]
    public async Task UpdateLastReadByMessageAndChannelAsync(UpdateLastReadMessageByChannelDto dto)
    {
        var channel = await microserviceContext.Channels.Select(c => new
        {
            c.Id,
            c.GuildId
        }).FirstOrDefaultAsync(c => c.Id == dto.ChannelId);
        if (channel is null)
        {
            logger.LogWarning("Channel with ID {ChannelId} not found in context", dto.ChannelId);
            return;
        }

        var member = await microserviceContext.GuildMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == Context.UserIdentifier && m.GuildId == channel.GuildId);
        if (member is null)
        {
            logger.LogWarning("member with not found");
            return;
        }

        var lastRead =
            await microserviceContext.ReadStates.FirstOrDefaultAsync(r =>
                r.ChannelId == dto.ChannelId && r.MemberId == member.Id);
        if (lastRead is null)
        {
            lastRead = new ReadState
            {
                Id = ReadState.GenerateId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ChannelId = dto.ChannelId,
                MemberId = member.Id
            };
            await microserviceContext.ReadStates.AddAsync(lastRead);
        }

        lastRead.LastReadMessageId = dto.Id;
        lastRead.MentionCount = 0;
        lastRead.UpdatedAt = DateTime.UtcNow;
        await microserviceContext.SaveChangesAsync();
    }

    [HubMethodName("StartTyping")]
    public async Task StartTyping(string channelId)
    {
        var userId = Context.UserIdentifier;

        var cacheId = $"channel_map:{channelId}";

        var cachedGuildId = await cache.GetStringAsync(cacheId);
        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
            var channel = await microserviceContext.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);

            cachedGuildId = channel?.GuildId;
            if (string.IsNullOrWhiteSpace(cachedGuildId)) return;
            await cache.SetStringAsync(cacheId, cachedGuildId);
        }



        var presence = await service.GetGuildPresenceAsync(cachedGuildId);


        await Clients.Users(presence.Select(p => p.UserId)).SendAsync("UserTyping", new UserTypingEvent
        {
            ChannelId = channelId,
            UserId = userId
        });
    }


    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected from guild hub, id {ConnectionId}, userId {UserId}",
            Context.ConnectionId, Context.UserIdentifier);

        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await base.OnDisconnectedAsync(exception);
            return;
        }

        var userId = Context.UserIdentifier!;

        var locationJson = await cache.GetStringAsync(ChannelVoiceState.GetUserCacheKey(userId));
        if (locationJson is not null)
        {
            var location = JsonSerializer.Deserialize<UserVoiceLocation>(locationJson);
            if (location is not null)
            {
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

                        await Clients.Users(onlineUserIds).SendAsync("UserLeftVoice",
                            new { userId, channelId = location.ChannelId, guildId = location.GuildId });
                    }
                }

                await cache.RemoveAsync(ChannelVoiceState.GetUserCacheKey(userId));
                await cache.RemoveAsync(ChannelVoiceState.GetHeartbeatCacheKey(userId));
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    [HubMethodName("VoiceHeartbeat")]
    public async Task VoiceHeartbeat()
    {
        var userId = Context.UserIdentifier!;
        await cache.SetStringAsync(
            ChannelVoiceState.GetHeartbeatCacheKey(userId),
            "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90) });
    }

    [HubMethodName("VoiceMuteChanged")]
    public async Task MuteChanged(VoiceMuteChangedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsSelfMuted = dto.IsMuted;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
                JsonSerializer.Serialize(voiceState),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await Clients.Users(otherIds).SendAsync("MuteChanged",
            new { userId, isMuted = dto.IsMuted, channelId = dto.ChannelId, serverForced = false });
    }

    [HubMethodName("VoiceDeafenChanged")]
    public async Task DeafenChanged(VoiceDeafenChangedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsSelfDeafened = dto.IsDeafened;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
                JsonSerializer.Serialize(voiceState),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await Clients.Users(otherIds).SendAsync("DeafenChanged",
            new { userId, isDeafened = dto.IsDeafened, channelId = dto.ChannelId, serverForced = false });
    }

    [HubMethodName("VoiceCameraChanged")]
    public async Task CameraChanged(VoiceCameraChangedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await Clients.Users(otherIds).SendAsync("CameraChanged",
            new { userId, isCameraOn = dto.IsCameraOn, channelId = dto.ChannelId });
    }

    [HubMethodName("VoiceScreenShareStarted")]
    public async Task ScreenShareStarted(VoiceScreenShareStartedDto dto)
    {
        var userId = Context.UserIdentifier!;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

        var guildId = voiceState.GuildId;
        var canStream = await permissionService.CanUserPerformActionAsync(userId, dto.ChannelId, Permissions.Stream);
        if (!canStream) return;

        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsStreaming = true;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
                JsonSerializer.Serialize(voiceState),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await Clients.Users(otherIds).SendAsync("ScreenShareStarted",
            new { userId, shareId = dto.ShareId, trackName = dto.TrackName, channelId = dto.ChannelId });
    }

    [HubMethodName("VoiceScreenShareStopped")]
    public async Task ScreenShareStopped(VoiceScreenShareStoppedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;

        var me = voiceState.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is not null)
        {
            me.IsStreaming = false;
            await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
                JsonSerializer.Serialize(voiceState),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });
        }

        var otherIds = voiceState.Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();
        await Clients.Users(otherIds).SendAsync("ScreenShareStopped",
            new { shareId = dto.ShareId, channelId = dto.ChannelId });
    }

    [HubMethodName("VoiceServerMute")]
    public async Task ServerMute(VoiceServerMuteDto dto)
    {
        var userId = Context.UserIdentifier!;
        var canMute = await permissionService.CanUserPerformActionAsync(userId, dto.ChannelId, Permissions.MuteMembers);
        if (!canMute) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var target = voiceState.Participants.FirstOrDefault(p => p.UserId == dto.TargetUserId);
        if (target is null) return;

        target.IsServerMuted = dto.IsMuted;
        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
            JsonSerializer.Serialize(voiceState),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

        var allIds = voiceState.Participants.Select(p => p.UserId).ToList();
        await Clients.Users(allIds).SendAsync("MuteChanged",
            new { userId = dto.TargetUserId, isMuted = dto.IsMuted, channelId = dto.ChannelId, serverForced = true });
    }

    [HubMethodName("VoiceServerDeafen")]
    public async Task ServerDeafen(VoiceServerDeafenDto dto)
    {
        var userId = Context.UserIdentifier!;
        var canDeafen = await permissionService.CanUserPerformActionAsync(userId, dto.ChannelId, Permissions.DeafenMembers);
        if (!canDeafen) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var target = voiceState.Participants.FirstOrDefault(p => p.UserId == dto.TargetUserId);
        if (target is null) return;

        target.IsServerDeafened = dto.IsDeafened;
        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
            JsonSerializer.Serialize(voiceState),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

        var allIds = voiceState.Participants.Select(p => p.UserId).ToList();
        await Clients.Users(allIds).SendAsync("DeafenChanged",
            new { userId = dto.TargetUserId, isDeafened = dto.IsDeafened, channelId = dto.ChannelId, serverForced = true });
    }

    [HubMethodName("VoiceMoveUser")]
    public async Task MoveUser(VoiceMoveUserDto dto)
    {
        var userId = Context.UserIdentifier!;
        var canMove = await permissionService.CanUserPerformActionAsync(userId, dto.ChannelId, Permissions.MoveMembers);
        if (!canMove) return;

        var raw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId));
        if (raw is null) return;

        var voiceState = JsonSerializer.Deserialize<ChannelVoiceState>(raw)!;
        var target = voiceState.Participants.FirstOrDefault(p => p.UserId == dto.TargetUserId);
        if (target is null) return;

        // Remove from current channel
        voiceState.Participants.Remove(target);
        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.ChannelId),
            JsonSerializer.Serialize(voiceState),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

        var onlineUserIds = await microserviceContext.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == voiceState.GuildId)
            .Select(m => m.UserId)
            .ToListAsync();

        await Clients.Users(onlineUserIds).SendAsync("UserLeftVoice",
            new { userId = dto.TargetUserId, channelId = dto.ChannelId, guildId = voiceState.GuildId });

        // Add to target channel
        var targetRaw = await cache.GetStringAsync(ChannelVoiceState.GetCacheKey(dto.TargetChannelId));
        var targetVoiceState = targetRaw is not null
            ? JsonSerializer.Deserialize<ChannelVoiceState>(targetRaw)!
            : new ChannelVoiceState { ChannelId = dto.TargetChannelId, GuildId = voiceState.GuildId };

        targetVoiceState.Participants.Add(new VoiceState
        {
            UserId = dto.TargetUserId,
            ChannelId = dto.TargetChannelId,
            GuildId = voiceState.GuildId,
            JoinedAt = DateTime.UtcNow
        });

        await cache.SetStringAsync(ChannelVoiceState.GetCacheKey(dto.TargetChannelId),
            JsonSerializer.Serialize(targetVoiceState),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

        await cache.SetStringAsync(
            ChannelVoiceState.GetUserCacheKey(dto.TargetUserId),
            JsonSerializer.Serialize(new UserVoiceLocation { ChannelId = dto.TargetChannelId, GuildId = voiceState.GuildId }),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(4) });

        await Clients.Users(onlineUserIds).SendAsync("UserJoinedVoice",
            new { userId = dto.TargetUserId, channelId = dto.TargetChannelId, guildId = voiceState.GuildId });

        // Notify the moved user specifically
        await Clients.User(dto.TargetUserId).SendAsync("MovedToChannel",
            new { channelId = dto.TargetChannelId, guildId = voiceState.GuildId, movedBy = userId });
    }
}