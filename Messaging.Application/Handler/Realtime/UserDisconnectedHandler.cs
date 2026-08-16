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

        // The presence work above repairs itself on the next connect.
        if (cmd.ServerStopping) return;

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

        // A trail, left deliberately, and the only reason the reconnect banner can offer a call
        // back.
        await cache.SetStringAsync(RecentCallKey(cmd.UserId), callId, RecentCallOptions);
    }

    /// <summary>How long after a drop the call is still worth offering back.</summary>
    public static readonly DistributedCacheEntryOptions RecentCallOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    /// <summary>Reverse index answering "was this user just dropped out of a call", written here
    /// and read by <c>VoiceController.GetActiveCall</c>.</summary>
    public static string RecentCallKey(string userId) => $"user-recent-call:{userId}";
}
