using Echo.Realtime;
using Identity.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Messaging.Application.Handler.Devices;

/// <summary>Tells the account's devices that a new one appeared.</summary>
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
