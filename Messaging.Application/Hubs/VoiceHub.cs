using System.Text.Json;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Hub = Microsoft.AspNetCore.SignalR.Hub;
using Wolverine;

namespace Messaging.Application.Hubs;

public record MuteChangedDto(string CallId, bool IsMuted);
public record CameraChangedDto(string CallId, bool IsCameraOn);
public record ScreenShareStartedDto(string CallId, string ShareId, string TrackName);
public record ScreenShareStoppedDto(string CallId, string ShareId);
public record SpeakingChangedDto(string CallId, bool IsSpeaking);

[Authorize]
public class VoiceHub(ILogger<VoiceHub> logger, MicroserviceContext context, IMessageBus bus, IDistributedCache cache) : Hub
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected to voice hub, id {ConnectionId}, userId {userId}", Context.ConnectionId, Context.UserIdentifier);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected from voice hub, id {ConnectionId}, userId {userId}", Context.ConnectionId, Context.UserIdentifier);

        var userId = Context.UserIdentifier!;
        var callId = await cache.GetStringAsync($"user-call:{userId}");
        if (callId is not null)
        {
            var raw = await cache.GetStringAsync(Call.GetCacheId(callId));
            if (raw is not null)
            {
                var call = JsonSerializer.Deserialize<Call>(raw)!;
                var otherIds = call.Participants
                    .Where(p => p.UserId != userId)
                    .Select(p => p.UserId)
                    .ToList();
                await Clients.Users(otherIds).SendAsync("ParticipantLeft", new { userId });
            }
            await cache.RemoveAsync($"user-call:{userId}");
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task MuteChanged(MuteChangedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(Call.GetCacheId(dto.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != userId)
            .Select(p => p.UserId)
            .ToList();

        await Clients.Users(otherIds).SendAsync("MuteChanged", new { userId, isMuted = dto.IsMuted });
    }

    public async Task CameraChanged(CameraChangedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(Call.GetCacheId(dto.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != userId)
            .Select(p => p.UserId)
            .ToList();

        await Clients.Users(otherIds).SendAsync("CameraChanged", new { userId, isCameraOn = dto.IsCameraOn });
    }

    public async Task ScreenShareStarted(ScreenShareStartedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(Call.GetCacheId(dto.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != userId)
            .Select(p => p.UserId)
            .ToList();

        var cfSessionId = call.Participants
            .FirstOrDefault(p => p.UserId == userId)?.CfSessionId;

        await Clients.Users(otherIds).SendAsync("ScreenShareStarted",
            new { shareId = dto.ShareId, userId, cfSessionId, trackName = dto.TrackName });
    }

    public async Task SpeakingChanged(SpeakingChangedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(Call.GetCacheId(dto.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != userId)
            .Select(p => p.UserId)
            .ToList();

        await Clients.Users(otherIds).SendAsync("SpeakingChanged", new { userId, isSpeaking = dto.IsSpeaking });
    }

    public async Task ScreenShareStopped(ScreenShareStoppedDto dto)
    {
        var userId = Context.UserIdentifier!;
        var raw = await cache.GetStringAsync(Call.GetCacheId(dto.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != userId)
            .Select(p => p.UserId)
            .ToList();

        await Clients.Users(otherIds).SendAsync("ScreenShareStopped", new { shareId = dto.ShareId });
    }
}
