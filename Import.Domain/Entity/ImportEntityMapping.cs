using Import.Domain.Enums;
using Persistence;

namespace Import.Domain.Entity;

/// <summary>Persisted Discord-id -&gt; Echo-id mapping for a linked guild.</summary>
public class ImportEntityMapping : BaseEntity<ImportEntityMapping>, IPrefixedEntity
{
    public static string Prefix { get; } = "imem";

    public string GuildLinkId { get; init; }
    public string DiscordId { get; init; }
    public ImportEntityType EntityType { get; init; }
    public string EchoId { get; set; }
}
