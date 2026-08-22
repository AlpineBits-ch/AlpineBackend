using Persistence;

namespace Discovery.Domain.Entities;

public class GameTopic : BaseEntity<GameTopic>, IPrefixedEntity
{
    public static string Prefix { get; } = "gmtp";

    /// <summary>Social's `gapp_` id. The topic id, not this row's id.</summary>
    public string GameApplicationId { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string[] Aliases { get; set; } = [];

    /// <summary>Mirrored for the cross-instance topic key federation will need. Unread in v1.</summary>
    public string? SteamAppId { get; set; }

    public bool IsEnabled { get; set; } = true;
}
