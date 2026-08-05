using Persistence;

namespace Social.Domain.Aggregate;

/// <summary>A single row recording which bootstrap seed version has been applied.</summary>
public class GameCatalogState : BaseEntity<GameCatalogState>, IPrefixedEntity
{
    public static string Prefix { get; } = "gcst";

    /// <summary>The one row's id.</summary>
    public const string SingletonId = "gcst_singleton";

    /// <summary>The <c>version</c> field of the applied seed artifact.</summary>
    public string SeedVersion { get; set; } = null!;

    public DateTimeOffset SeededAt { get; set; }
}
