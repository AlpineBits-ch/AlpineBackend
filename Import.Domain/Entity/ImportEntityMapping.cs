using Import.Domain.Enums;
using Persistence;

namespace Import.Domain.Entity;

/// <summary>
/// Persisted Discord-id -&gt; Echo-id mapping for a linked guild. A one-shot import could get
/// away with an in-memory map while building its bulk create command, but ongoing sync needs
/// this to survive across events/restarts - e.g. a later GUILD_ROLE_UPDATE has to find which
/// existing Echo Role corresponds to that Discord role id.
/// </summary>
public class ImportEntityMapping : BaseEntity<ImportEntityMapping>, IPrefixedEntity
{
    public static string Prefix { get; } = "imem";

    public string GuildLinkId { get; init; }
    public string DiscordId { get; init; }
    public ImportEntityType EntityType { get; init; }
    public string EchoId { get; set; }
}
