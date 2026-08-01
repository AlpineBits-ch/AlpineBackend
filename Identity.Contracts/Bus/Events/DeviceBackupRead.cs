namespace Identity.Contracts.Bus.Events;

/// <summary>
/// Somebody downloaded one of this account's encrypted device backups.
///
/// <para>Identity owns the blobs but hosts no SignalR hub, so the notice takes the bus to a service
/// that does - the same shape as Messaging's channel events going to Guild - rather than standing
/// up a second realtime stack here.</para>
///
/// <para>This is the whole point of the audit rule: a stolen session that downloads every device's
/// backup changes nothing observable otherwise. The account's other devices are told immediately so
/// the user can act while the material is still worth protecting.</para>
/// </summary>
public class DeviceBackupRead
{
    public string UserId { get; init; } = null!;

    /// <summary>Client device id whose backup was read.</summary>
    public string DeviceId { get; init; } = null!;

    public string DeviceName { get; init; } = null!;

    /// <summary>Client device id that performed the read, so the user's other devices can say
    /// "your laptop read your phone's backup" rather than just "something happened".</summary>
    public string? ReadByDeviceId { get; init; }

    public DateTimeOffset ReadAt { get; init; }
}
