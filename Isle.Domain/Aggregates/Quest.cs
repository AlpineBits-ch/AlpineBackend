using Domain;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Persistence;

namespace Isle.Domain.Aggregates;

/// <summary>
/// A quest <i>template</i>: the thing an admin authors once. The director reads the enabled rows,
/// picks one, and writes a <see cref="QuestInstance"/> — the template itself is never mutated except
/// for <see cref="LastSpawnedAt"/>.
/// </summary>
public class Quest : Aggregate<Quest>, IPrefixedEntity
{
    public string Name { get; set; }
    public string Description { get; set; }
    public static string Prefix { get; } = "quest";

    public QuestType Type { get; set; } = QuestType.Exploration;

    /// <summary>Disabled templates are invisible to the director but stay available to <c>!questadmin spawn</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Relative pick weight among otherwise equally-scored candidates. 0 or less is treated as 1.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>How long an instance stays open before it expires unfulfilled.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Minimum gap between two instances of this template, measured from the last spawn.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(45);

    /// <summary>The director skips this template when fewer players than this are online.</summary>
    public int MinOnlinePlayers { get; set; } = 1;

    /// <summary>
    /// Chat line for the spawn broadcast. <c>{location}</c> and <c>{coords}</c> are substituted;
    /// blank falls back to a generic line built from <see cref="Name"/>.
    /// </summary>
    public string? AnnouncementTemplate { get; set; }

    public DateTimeOffset? LastSpawnedAt { get; set; }

    public ICollection<RewardConfig> Rewards { get; } = new List<RewardConfig>();

    public ICollection<QuestLocation> Locations { get; } = new List<QuestLocation>();

    /// <summary>
    /// Whether the director may spawn this template right now. Deliberately does not consider whether
    /// an instance is already live — that is a query the caller owns.
    /// </summary>
    public bool IsSpawnable(int onlinePlayers, DateTimeOffset now)
    {
        if (!Enabled) return false;
        if (Type == QuestType.Bounty) return false;      // bounties are targeted, never picked at random
        if (Locations.Count == 0) return false;
        if (onlinePlayers < MinOnlinePlayers) return false;

        return LastSpawnedAt is not { } last || now - last >= Cooldown;
    }

    public int EffectiveWeight => Weight > 0 ? Weight : 1;
}
