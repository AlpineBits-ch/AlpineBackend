using System.Text.Json;
using Echo.Realtime;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Handler.Realtime;

public class UserDisconnectedHandler
{
    public static async Task Handle(UserDisconnected cmd, IMessageBus bus, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        // MessagingHub.OnDisconnectedAsync: presence
        await bus.PublishAsync(new UserInactiveEvent() { UserId = cmd.UserId });

        var relationships = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = cmd.UserId });

        foreach (var relationship in relationships.Profile?.Relationships ?? [])
        {
            await hub.Clients.User(relationship.UserId).SendAsync("presence.UserOffline", cmd.UserId);
        }

        // VoiceHub.OnDisconnectedAsync: leave any active call
        var callId = await cache.GetStringAsync($"user-call:{cmd.UserId}");
        if (callId is not null)
        {
            var raw = await cache.GetStringAsync(Domain.Entities.Call.GetCacheId(callId));
            if (raw is not null)
            {
                var call = JsonSerializer.Deserialize<Domain.Entities.Call>(raw)!;
                var otherIds = call.Participants
                    .Where(p => p.UserId != cmd.UserId)
                    .Select(p => p.UserId)
                    .ToList();
                await hub.Clients.Users(otherIds).SendAsync("call.ParticipantLeft", new { userId = cmd.UserId });
            }
            await cache.RemoveAsync($"user-call:{cmd.UserId}");
        }
    }
}
