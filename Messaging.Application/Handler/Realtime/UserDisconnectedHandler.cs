using Echo.Realtime;
using Echo.Realtime.Caching;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Handler.Realtime;

public class UserDisconnectedHandler
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    public static async Task Handle(UserDisconnected cmd, IMessageBus bus, IDistributedCache cache,
        LockedJsonCacheStore callStore, IHubContext<EchoRealtimeHub> hub)
    {
        // MessagingHub.OnDisconnectedAsync: presence
        await bus.PublishAsync(new UserInactiveEvent() { UserId = cmd.UserId });

        var relationships = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = cmd.UserId });

        foreach (var relationship in relationships.Profile?.Relationships ?? [])
        {
            await hub.Clients.User(relationship.UserId).SendAsync("presence.UserOffline", cmd.UserId);
        }

        // VoiceHub.OnDisconnectedAsync: leave any active call.
        var callId = await cache.GetStringAsync($"user-call:{cmd.UserId}");
        if (callId is null) return;

        if (!string.IsNullOrWhiteSpace(cmd.DeviceId))
        {
            // Guarded inside Call.Leave: no-ops if this device isn't (or no longer is, e.g. it
            // was already superseded by a takeover) the participant's active device, so the old
            // device's own disconnect can't stomp on a different device's active call.
            var call = await callStore.UpdateAsync<Domain.Entities.Call>(
                Domain.Entities.Call.GetCacheId(callId), Domain.Entities.Call.GetCacheId(callId),
                c => c.Leave(cmd.UserId, cmd.DeviceId!), CacheOptions);

            if (call is not null)
            {
                foreach (var evt in call.GetDomainEvents())
                {
                    await bus.PublishAsync(evt);
                }

                if (call.Status == CallStatus.Completed)
                {
                    await CallEndNotifier.NotifyAsync(call, CallEndReason.AllParticipantsLeft, cmd.UserId, bus, cache, hub);
                }
            }
        }

        await cache.RemoveAsync($"user-call:{cmd.UserId}");
    }
}
