using Persistence;
using Social.Domain.Enums;

namespace Social.Domain.Aggregate;

/// <summary>One detectable game or application in the catalog.</summary>
public class GameApplication : BaseEntity<GameApplication>, IPrefixedEntity
{
    public static string Prefix { get; } = "gapp";

    /// <summary>
    /// The application id this game announces over the local RPC socket, when it has one.
    /// </summary>
    public string? DiscordApplicationId { get; set; }

    /// <summary>The canonical display name.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Alternate names, used for search and de-duplication. Not shown to users.</summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>
    /// Steam application id, when known - present for roughly three quarters of the catalog.
    /// </summary>
    public string? SteamAppId { get; set; }

    public GameCatalogSource Source { get; set; } = GameCatalogSource.Seeded;

    /// <summary>Suppresses an entry without deleting it - a bad bootstrap row can be switched off
    /// without losing the record that it was there.</summary>
    public bool IsEnabled { get; set; } = true;

    public virtual ICollection<GameExecutable> Executables { get; set; } = new List<GameExecutable>();
}
