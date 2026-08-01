using Echo.Realtime;
using Identity.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;

namespace Messaging.Application.Handler.Devices;

/// <summary>Fans Identity's account-security events out to the account's devices.</summary>
public class BackupReadHandler
{
    /// <summary>Someone downloaded an encrypted device backup.</summary>
    public async Task Handle(DeviceBackupRead message, IHubContext<EchoRealtimeHub> hub)
    {
        if (string.IsNullOrWhiteSpace(message.UserId)) return;

        await hub.Clients.User(message.UserId).SendAsync("identity.BackupRead", new
        {
            deviceId = message.DeviceId,
            deviceName = message.DeviceName,
            readByDeviceId = message.ReadByDeviceId,
            readAt = message.ReadAt,
        });
    }
}

/// <summary>The account's device-admission protection level changed.</summary>
public class ProtectionLevelChangedHandler
{
    public async Task Handle(ProtectionLevelChanged message, IHubContext<EchoRealtimeHub> hub)
    {
        if (string.IsNullOrWhiteSpace(message.UserId)) return;

        await hub.Clients.User(message.UserId).SendAsync("identity.ProtectionLevelChanged", new
        {
            previousLevel = message.PreviousLevel.ToString(),
            level = message.Level.ToString(),
            version = message.Version,
            signedAssertion = message.SignedAssertion,
            changedByDeviceId = message.ChangedByDeviceId,
            isDowngrade = message.IsDowngrade,
            changedAt = message.ChangedAt,
        });
    }
}

/// <summary>The account identity key was replaced.</summary>
public class AccountIdentityKeyRotatedHandler
{
    public async Task Handle(AccountIdentityKeyRotated message, IHubContext<EchoRealtimeHub> hub)
    {
        if (string.IsNullOrWhiteSpace(message.UserId)) return;

        await hub.Clients.User(message.UserId).SendAsync("identity.AccountIdentityKeyRotated", new
        {
            previousVersion = message.PreviousVersion,
            version = message.Version,
            publicKey = message.PublicKey,
            signedByOutgoingKey = message.SignedByOutgoingKey,
            changedByDeviceId = message.ChangedByDeviceId,
            rotatedAt = message.RotatedAt,
        });
    }
}
