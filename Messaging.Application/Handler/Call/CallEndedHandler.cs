using Messaging.Domain.Entities;
using Messaging.Domain.Events.Call;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Call;

public class CallEndedHandler
{
    public static Task Handle(CallEnded @event, IDistributedCache cache)
    {
        return cache.RemoveAsync(Domain.Entities.Call.GetCacheId(@event.CallId));
    }
}