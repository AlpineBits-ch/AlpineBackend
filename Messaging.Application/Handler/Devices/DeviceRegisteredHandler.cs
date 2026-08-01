using Echo.Realtime;
using Identity.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Messaging.Application.Handler.Devices;

/// <summary>
/// Tells the account's devices that a new one appeared.
///
/// <para><c>DeviceRegistered</c> had no consumer anywhere in the solution, which is most of why a
/// device registered after a conversation already existed stayed permanently outside it: nothing
/// told anybody it had shown up. The server cannot admit it - only a member's client can produce an
/// Add commit - so what happens here is the part the server can do, which is make the appearance
/// visible.</para>
///
/// <para>The admission itself is client-driven from here: the new device lists its conversations,
/// asks each encrypted one it holds no group for for admission, and an existing device reviews it.
/// This push exists so the existing devices find out immediately rather than at their next launch,
/// and - for a registration that <b>rotated</b> the identity key - so they learn that the device's
/// old leaf can no longer sign for itself and has to be replaced rather than topped up.</para>
/// </summary>
public class DeviceRegisteredHandler
{
    public async Task Handle(
        DeviceRegistered message,
        IHubContext<EchoRealtimeHub> hub,
        ILogger<DeviceRegisteredHandler> logger)
    {
        if (string.IsNullOrWhiteSpace(message.UserId)) return;

        await hub.Clients.User(message.UserId).SendAsync("identity.DeviceRegistered", new
        {
            deviceId = message.ClientDeviceId,
            deviceName = message.DeviceName,
            identityRotated = message.IdentityRotated,
        });

        if (message.IdentityRotated)
        {
            logger.LogInformation(
                "Device {DeviceId} of user {UserId} rotated its identity key; its key packages were purged",
                message.ClientDeviceId, message.UserId);
        }
    }
}
