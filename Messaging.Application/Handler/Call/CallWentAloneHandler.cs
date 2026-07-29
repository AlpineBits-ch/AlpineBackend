using Echo.Realtime;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Messaging.Application.Handler.Call;

public class CallWentAloneHandler
{
    /// <summary>How long the sole remaining participant gets before the call is auto-ended.</summary>
    private static readonly TimeSpan AloneTimeout = TimeSpan.FromMinutes(5);

    public static async Task<object> Handle(CallWentAlone @event, IHubContext<EchoRealtimeHub> hub)
    {
        var deadline = @event.AloneSince + AloneTimeout;
        await hub.Clients.User(@event.UserId).SendAsync("call.CallAlone",
            new { callId = @event.CallId, userId = @event.UserId, deadline });

        return new CallAloneTimeoutCheck
        {
            CallId = @event.CallId,
            ExpectedAloneSince = @event.AloneSince,
        }.DelayedFor(AloneTimeout);
    }
}
