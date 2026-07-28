namespace Import.Domain.Enums;

/// <summary>
/// Only <see cref="DiscordToVenta"/> is implemented today. The other two values exist so a
/// future Venta-&gt;Discord sync (subscribing to Guild's existing ChannelCreatedForBots/etc. bus
/// events and translating them into Discord REST calls) can be added without a schema change.
/// </summary>
public enum SyncDirection
{
    DiscordToVenta,
    VentaToDiscord,
    Bidirectional,
}
