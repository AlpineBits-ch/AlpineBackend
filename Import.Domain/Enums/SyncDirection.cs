namespace Import.Domain.Enums;

/// <summary>Only <see cref="DiscordToVenta"/> is implemented today.</summary>
public enum SyncDirection
{
    DiscordToVenta,
    VentaToDiscord,
    Bidirectional,
}
