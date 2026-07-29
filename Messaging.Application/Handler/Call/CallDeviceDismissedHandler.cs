using Echo.Realtime;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;

namespace Messaging.Application.Handler.Call;

/// <summary>Tells exactly the device that sent a stale decline to stop ringing - the call itself
/// is untouched, since the user already answered from another device.</summary>
public class CallDeviceDismissedHandler
{
    public static Task Handle(CallDeviceDismissed @event, IHubContext<EchoRealtimeHub> hub) =>
        hub.Clients.Group(EchoRealtimeHub.DeviceGroup(@event.UserId, @event.DeviceId))
            .SendAsync("call.CallDeviceDismissed", new { callId = @event.CallId, deviceId = @event.DeviceId });
}
