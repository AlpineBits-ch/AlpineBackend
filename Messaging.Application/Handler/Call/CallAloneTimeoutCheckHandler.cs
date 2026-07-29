using Echo.Realtime;
using Echo.Realtime.Caching;
using Messaging.Application.Services;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Messaging.Application.Handler.Call;

public class CallAloneTimeoutCheckHandler
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    public static async Task Handle(CallAloneTimeoutCheck @event, IHubContext<EchoRealtimeHub> hubContext,
        IDistributedCache cache, LockedJsonCacheStore callStore, IMessageBus bus)
    {
        // Locked, same check-then-act-under-lock shape as CallRingTimeoutCheckHandler: guards
        // against someone rejoining (or the sole survivor also leaving) in the same window this
        // fires.
        var didTimeout = false;
        var call = await callStore.UpdateAsync<Domain.Entities.Call>(
            Domain.Entities.Call.GetCacheId(@event.CallId), Domain.Entities.Call.GetCacheId(@event.CallId),
            c =>
            {
                var wasAlreadyOver = c.Status == CallStatus.Completed;
                c.EndIfStillAlone(@event.ExpectedAloneSince);
                didTimeout = !wasAlreadyOver && c.Status == CallStatus.Completed;
            }, CacheOptions);
        if (call == null || !didTimeout) return;

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await CallEndNotifier.NotifyAsync(call, CallEndReason.AloneTimeout, null, bus, cache, hubContext);
    }
}
