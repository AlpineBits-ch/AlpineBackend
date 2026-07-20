using Echo.Realtime;

using Messaging.Application.Services;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Call;

public class CallAcceptedHandler
{
    public static async Task Handle(CallAccepted @event, IHubContext<EchoRealtimeHub> hubContext, IDistributedCache cache)
    {
        var call = await CallService.GetCallById(@event.CallId, cache);
        if (call == null)
        {
            throw new Exception("Call not found, cannot end call");
        }
        
        await hubContext.Clients.Users(call.Participants.Select(p => p.UserId)).SendAsync("CallAccepted", call);
    }
}