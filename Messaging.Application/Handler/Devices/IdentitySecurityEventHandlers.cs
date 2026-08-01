using Echo.Realtime;
using Identity.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;

namespace Messaging.Application.Handler.Devices;

/// <summary>
/// Fans Identity's account-security events out to the account's devices.
///
/// <para>Identity owns this state but hosts no SignalR hub, so these take the bus to a service that
/// does - the same shape as Messaging's channel events going to Guild. They are grouped here because
/// they are one idea: <b>something changed about how this account protects itself, and every device
/// has to hear about it immediately.</b> Each of these is a step an attacker holding a session would
/// take, and each is undetectable if it is only written to a database.</para>
/// </summary>
public class BackupReadHandler
{
    /// <summary>
    /// Someone downloaded an encrypted device backup.
    ///
    /// <para>The one operation in the backup system that leaks everything and leaves no trace
    /// otherwise: no blob changes, nothing is deleted, and the legitimate owner has no way to
    /// notice. Exfiltration that cannot be prevented is at least made visible.</para>
    /// </summary>
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

/// <summary>
/// The account's device-admission protection level changed.
///
/// <para>Broadcast unconditionally, including on an upgrade. A downgrade from VerifiedDevices is the
/// move that makes auto-admitting an attacker's device possible, and a client that sees one it did
/// not participate in is supposed to warn loudly - which it can only do if it is told.</para>
/// </summary>
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

/// <summary>
/// The account identity key was replaced.
///
/// <para>Invalidates every peer's pinning and every device certificate issued under the old key, so
/// it is a security event rather than a routine write. A rotation that could not be signed by the
/// outgoing key is carried through as such: peers must re-verify out of band, and auto-accepting
/// would turn rotation into a way in.</para>
/// </summary>
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
