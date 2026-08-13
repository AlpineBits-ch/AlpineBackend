using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Persistence;

namespace Isle.Domain.Entity;

/// <summary>What one player did on one quest run, written once when the run resolves.</summary>
public class QuestParticipation : BaseEntity<QuestParticipation>, IPrefixedEntity
{
    public static string Prefix { get; } = "qpart";

    public string QuestInstanceId { get; set; } = string.Empty;
    public virtual QuestInstance? QuestInstance { get; set; }

    public string PlayerId { get; set; } = string.Empty;
    public virtual Player? Player { get; set; }

    /// <summary>What the player achieved, in the unit the quest is measured in: dwell samples for an
    /// exploration, kills for a hunt.</summary>
    public int Progress { get; set; }

    /// <summary>What was needed.</summary>
    public int Goal { get; set; }

    /// <summary>
    /// The tier this showing earned, or null when the player turned up but did not qualify.
    /// </summary>
    public RankRequirement? Rank { get; set; }

    /// <summary>True only when something actually reached the player.</summary>
    public bool WasPaid { get; set; }

    /// <summary>The payout lines exactly as the player was told them, joined.</summary>
    public string RewardSummary { get; set; } = string.Empty;

    /// <summary>How the run ended, copied from the instance so a player's history reads without a join
    /// back to it.</summary>
    public QuestInstanceState Outcome { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public static QuestParticipation Record(
        string questInstanceId,
        string playerId,
        QuestInstanceState outcome,
        int progress,
        int goal,
        RankRequirement? rank,
        IReadOnlyCollection<string>? rewards)
    {
        var now = DateTimeOffset.UtcNow;
        var paid = rewards is { Count: > 0 };

        return new QuestParticipation
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            RecordedAt = now,
            QuestInstanceId = questInstanceId,
            PlayerId = playerId,
            Outcome = outcome,
            Progress = progress < 0 ? 0 : progress,
            Goal = goal < 0 ? 0 : goal,
            Rank = rank,
            WasPaid = paid,
            RewardSummary = paid ? string.Join(", ", rewards!) : string.Empty,
        };
    }
}
