using Isle.Domain.Aggregates;
using Isle.Domain.ValueObjects;
using Persistence;

namespace Isle.Domain.Entity;

/// <summary>
/// Where a quest can be spawned. <see cref="RegionId"/> is the field the director actually scores on
/// — it keys into <see cref="MapRegion.GetRegions"/> and is what lets a spawn be pushed away from
/// crowded parts of the map. <see cref="GeoFence"/> is the finer-grained shape for later (proximity
/// completion checks); nothing reads it yet.
/// </summary>
public class QuestLocation : BaseEntity<QuestLocation>, IPrefixedEntity
{
    public string Title { get; set; }
    public string Description { get; set; }

    public static string Prefix { get; } = "quest_location";

    /// <summary>Id of the owning <see cref="MapRegion"/>. Null means the location is unmapped and the director will skip it.</summary>
    public string? RegionId { get; set; }

    public GeoFenceData GeoFence { get; set; }

    public virtual ICollection<Quest> Quests { get; set; }
}
