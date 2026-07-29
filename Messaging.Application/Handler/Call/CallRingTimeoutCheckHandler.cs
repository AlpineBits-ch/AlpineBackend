using Echo.Realtime;
using Echo.Realtime.Caching;
using Messaging.Application.Services;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Messaging.Application.Handler.Call;

public class CallRingTimeoutCheckHandler
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    public static async Task Handle(CallRingTimeoutCheck @event, IHubContext<EchoRealtimeHub> hubContext,
        IDistributedCache cache, LockedJsonCacheStore callStore, IMessageBus bus)
    {
        // Locked: guards the Pending check-then-act against Accept/Decline landing in the
        // same window (e.g. the ring timeout firing right as the callee accepts). The "was
        // I the one who just timed it out" flag has to be captured from inside the mutate
        // delegate -checking the final Status afterwards can't distinguish "I just timed
        // this out" from "it was already Rejected for some other reason", which would
        // wrongly re-fire the cancel-call notifications below for an already-handled call.
        var didTimeout = false;
        var call = await callStore.UpdateAsync<Domain.Entities.Call>(
            Domain.Entities.Call.GetCacheId(@event.CallId), Domain.Entities.Call.GetCacheId(@event.CallId),
            c =>
            {
                if (c.Status != CallStatus.Pending) return;
                c.Timeout();
                didTimeout = true;
            }, CacheOptions);
        if (call == null || !didTimeout) return; // already answered, declined, or ended

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await CallEndNotifier.NotifyAsync(call, CallEndReason.Declined, null, bus, cache, hubContext);
    }
}
