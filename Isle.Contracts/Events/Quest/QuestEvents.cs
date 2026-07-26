using Isle.Domain.Enums;

namespace Isle.Contracts.Events.Quest;

/// <summary>Published whenever a quest instance goes live.</summary>
public class QuestSpawnedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string QuestId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public QuestType Type { get; set; }
    public string? RegionId { get; set; }
    public string? LocationName { get; set; }
    public double? WorldX { get; set; }
    public double? WorldY { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A player crossed the killing-spree thresholds (or an admin marked them) and is now hunted.</summary>
public class PlayerMarkedAsBountyEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public string TargetSteamId { get; set; } = string.Empty;
    public string? TargetSpecies { get; set; }
    public int KillStreak { get; set; }
    public string? RegionId { get; set; }
    public string? LocationName { get; set; }
    public double? WorldX { get; set; }
    public double? WorldY { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsAdminSpawned { get; set; }
}

/// <summary>A bounty closed — claimed, survived, or cancelled. <see cref="ClaimedByPlayerId"/> is null unless someone killed the target.</summary>
public class BountyResolvedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public string? ClaimedByPlayerId { get; set; }
    public QuestInstanceState State { get; set; }
}
