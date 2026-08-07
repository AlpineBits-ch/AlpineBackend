using Echo.Realtime;
using Echo.Realtime.Sfu;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Messaging.Application.Handler.Call;

/// <summary>Tells the superseded device to tear itself down, and best-effort closes its stale
/// Cloudflare session server-side too (the device may be backgrounded/unreachable and never
/// process the push).</summary>
public class CallDeviceTakeoverHandler
{
    public static async Task Handle(CallDeviceTakeover @event, IHubContext<EchoRealtimeHub> hub,
        CloudflareService cfService, ILogger<CallDeviceTakeoverHandler> logger)
    {
        await hub.Clients.Group(EchoRealtimeHub.DeviceGroup(@event.UserId, @event.OldDeviceId))
            .SendAsync("call.CallDeviceTakeover", new
            {
                callId = @event.CallId,
                oldDeviceId = @event.OldDeviceId,
                newDeviceId = @event.NewDeviceId,
            });

        if (@event.OldMediaSessionId is null || @event.OldAudioTrackName is null) return;
        try
        {
            await cfService.CloseTracksAsync(@event.OldMediaSessionId, [@event.OldAudioTrackName]);
        }
        catch (CloudflareCallsException ex)
        {
            logger.LogWarning(ex,
                "Best-effort close of superseded call session {MediaSessionId} for user {UserId} failed - the old device should still tear down client-side from call.CallDeviceTakeover",
                @event.OldMediaSessionId, @event.UserId);
        }
    }
}
