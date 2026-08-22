using Persistence;

namespace Discovery.Domain.Entities;

public class GameTopic : BaseEntity<GameTopic>, IPrefixedEntity
{
    public static string Prefix { get; } = "gmtp";

    /// <summary>Social's `gapp_` id. The topic id, not this row's id.</summary>
    public string GameApplicationId { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string[] Aliases { get; set; } = [];

    /// <summary>
    /// Lower-invariant Name followed by every Alias, space separated. Denormalized so search can
    /// filter with one Contains() on a scalar column - a nested lambda over Aliases translates on
    /// Npgsql but EF's InMemory provider refuses any method call inside it, so array-element search
    /// cannot be one query that also runs under InMemory (see TopicResolver). Set by GameCatalogSync
    /// on both the insert and the update path; a renamed game with a stale value here just falls out
    /// of search until the next sync.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Mirrored for the cross-instance topic key federation will need. Unread in v1.</summary>
    public string? SteamAppId { get; set; }

    public bool IsEnabled { get; set; } = true;
}
