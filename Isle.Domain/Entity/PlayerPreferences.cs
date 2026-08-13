using Isle.Domain.Aggregates;
using Persistence;

namespace Isle.Domain.Entity;

/// <summary>The settings that belong to a player's life on the island, and only those.</summary>
public class PlayerPreferences : BaseEntity<PlayerPreferences>, IPrefixedEntity
{
    public static string Prefix { get; } = "pref";

    public string PlayerId { get; set; } = string.Empty;
    public virtual Player? Player { get; set; }

    /// <summary>Server going up, down or restarting.</summary>
    public bool NotifyServerStatus { get; set; } = true;

    public bool NotifyQuestComplete { get; set; } = true;

    /// <summary>Off by default: a death notification arrives at the least welcome possible moment.</summary>
    public bool NotifyDinoDeath { get; set; }

    /// <summary>Whether the player appears in the public leaderboard listing.</summary>
    public bool ShowOnLeaderboard { get; set; } = true;

    /// <summary>Whether anyone may look this player up by their friendly id.</summary>
    public bool PublicProfile { get; set; }

    /// <summary>
    /// The one declaration of the default state, used both to create a row and to answer for a
    /// player who has none.
    /// </summary>
    public static PlayerPreferences For(string playerId)
    {
        var now = DateTimeOffset.UtcNow;

        return new PlayerPreferences
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            PlayerId = playerId,
        };
    }
}
