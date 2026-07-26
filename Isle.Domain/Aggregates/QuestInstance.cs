using Domain;
using Isle.Domain.Enums;
using Persistence;

namespace Isle.Domain.Aggregates;

public class SpawnQuestInstanceArgs
{
    public required string QuestId { get; init; }
    public required string Title { get; init; }
    public required TimeSpan Duration { get; init; }
    public QuestType Type { get; init; } = QuestType.Exploration;

    public string? LocationId { get; init; }
    public string? RegionId { get; init; }

    /// <summary>Human-readable place name as it was announced, snapshotted so history reads correctly after the region table changes.</summary>
    public string? LocationName { get; init; }

    public double? WorldX { get; init; }
    public double? WorldY { get; init; }

    /// <summary>The hunted player, for <see cref="QuestType.Bounty"/>.</summary>
    public string? TargetPlayerId { get; init; }

    public string? TargetSpecies { get; init; }

    /// <summary>True when an admin forced this instance rather than the director choosing it.</summary>
    public bool IsAdminSpawned { get; init; }

    /// <summary>
    /// Extra XP on top of the template's rewards, for an admin who wants this particular bounty to
    /// hurt. Null means "just the template".
    /// </summary>
    public int? BonusXp { get; init; }
}

/// <summary>One live (or historical) run of a <see cref="Quest"/> template.</summary>
public class QuestInstance : Aggregate<QuestInstance>, IPrefixedEntity
{
    public static string Prefix { get; } = "quest_instance";

    public string QuestId { get; set; } = string.Empty;
    public virtual Quest? Quest { get; set; }

    public QuestType Type { get; set; }
    public QuestInstanceState State { get; set; } = QuestInstanceState.Active;

    /// <summary>Announcement-time title, kept even if the template is renamed later.</summary>
    public string Title { get; set; } = string.Empty;

    public string? LocationId { get; set; }
    public string? RegionId { get; set; }
    public string? LocationName { get; set; }

    public double? WorldX { get; set; }
    public double? WorldY { get; set; }

    public string? TargetPlayerId { get; set; }
    public virtual Player? TargetPlayer { get; set; }
    public string? TargetSpecies { get; set; }

    public string? CompletedByPlayerId { get; set; }
    public virtual Player? CompletedByPlayer { get; set; }

    public bool IsAdminSpawned { get; set; }

    /// <summary>Admin-set XP on top of the template rewards. See <see cref="SpawnQuestInstanceArgs.BonusXp"/>.</summary>
    public int? BonusXp { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public bool IsOpen => State == QuestInstanceState.Active;

    public bool HasExpired(DateTimeOffset now) => IsOpen && now >= ExpiresAt;

    public static QuestInstance Spawn(SpawnQuestInstanceArgs args)
    {
        var now = DateTimeOffset.UtcNow;

        return new QuestInstance
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            QuestId = args.QuestId,
            Type = args.Type,
            State = QuestInstanceState.Active,
            Title = args.Title,
            LocationId = args.LocationId,
            RegionId = args.RegionId,
            LocationName = args.LocationName,
            WorldX = args.WorldX,
            WorldY = args.WorldY,
            TargetPlayerId = args.TargetPlayerId,
            TargetSpecies = args.TargetSpecies,
            IsAdminSpawned = args.IsAdminSpawned,
            BonusXp = args.BonusXp,
            StartedAt = now,
            ExpiresAt = now.Add(args.Duration),
        };
    }

    /// <summary>Closes the instance.</summary>
    public bool TryClose(QuestInstanceState state, string? completedByPlayerId = null)
    {
        if (!IsOpen || state == QuestInstanceState.Active)
            return false;

        State = state;
        CompletedByPlayerId = completedByPlayerId;
        EndedAt = DateTimeOffset.UtcNow;
        return true;
    }
}
