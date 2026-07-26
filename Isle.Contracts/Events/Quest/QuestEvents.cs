using Isle.Domain.Enums;

namespace Isle.Contracts.Events.Quest;

/// <summary>Published whenever a quest instance goes live.</summary>
public class QuestSpawnedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;

    /// <summary>The short id players see in chat — carried so a subscriber can match a run to what was announced.</summary>
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;

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
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;
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

/// <summary>A marked player died and the bounty is waiting to be resolved.</summary>
public class ResolveBountyDeathEvent
{
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>The bounty that was open when they died.</summary>
    public string QuestInstanceId { get; set; } = string.Empty;
}

/// <summary>A bounty closed — claimed, survived, or cancelled.</summary>
public class BountyResolvedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public string? ClaimedByPlayerId { get; set; }
    public QuestInstanceState State { get; set; }

    /// <summary>Everyone who did enough damage to the target to count, hardest hitter first.</summary>
    public List<string> ParticipantPlayerIds { get; set; } = [];
}

/// <summary>A quest instance closed with nobody fulfilling it. The counterpart to <see cref="QuestSpawnedEvent"/>.</summary>
public class QuestInstanceExpiredEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;
    public string QuestId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public QuestType Type { get; set; }
    public string? RegionId { get; set; }
    public string? LocationName { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>One player's cut of a resolved quest, as it was actually paid rather than as it was authored.</summary>
public class QuestRewardGrant
{
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>The tier this player was paid at.</summary>
    public RankRequirement Rank { get; set; }

    /// <summary>The payout lines the player was shown, e.g. <c>"2,500 XP"</c>, <c>"+5% growth"</c>.</summary>
    public List<string> Rewards { get; set; } = [];
}

/// <summary>What a resolved quest actually paid, to whom, in one message.</summary>
public class QuestRewardsGrantedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;
    public QuestType Type { get; set; }
    public List<QuestRewardGrant> Grants { get; set; } = [];
}
