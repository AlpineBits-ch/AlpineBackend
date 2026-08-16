using Echo.Realtime;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;

namespace Messaging.Application.Handler.Call;

/// <summary>Tells the superseded device to tear itself down.</summary>
public class CallDeviceTakeoverHandler
{
    public static async Task Handle(CallDeviceTakeover @event, IHubContext<EchoRealtimeHub> hub)
    {
        await hub.Clients.Group(EchoRealtimeHub.DeviceGroup(@event.UserId, @event.OldDeviceId))
            .SendAsync("call.CallDeviceTakeover", new
            {
                callId = @event.CallId,
                oldDeviceId = @event.OldDeviceId,
                newDeviceId = @event.NewDeviceId,
            });
    }
}
