namespace Identity.Contracts.Bus.Events;

/// <summary>Somebody downloaded one of this account's encrypted device backups.</summary>
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
