using Import.Domain.Enums;
using Persistence;

namespace Import.Domain.Entity;

/// <summary>A permanent Discord-server &lt;-&gt; Echo-guild link.</summary>
public class GuildLink : BaseEntity<GuildLink>, IPrefixedEntity
{
    public static string Prefix { get; } = "glnk";

    public string EchoGuildId { get; init; }
    public string DiscordGuildId { get; init; }
    public SyncDirection SyncDirection { get; set; } = SyncDirection.DiscordToVenta;
    public GuildLinkStatus Status { get; set; } = GuildLinkStatus.Active;
    public DateTimeOffset? LastSyncedAt { get; set; }
}
