using Isle.Domain.Enums;

namespace Isle.Contracts.Events.Quest;

/// <summary>
/// Published whenever a quest instance goes live. Nothing subscribes yet — these exist as the seam
/// the companion website (and any SignalR push) will hang off, so the quest services never need to
/// know a web front end exists.
/// </summary>
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

/// <summary>
/// A marked player died and the bounty is waiting to be resolved. Sent to itself on a delay, and only
/// ever when a bounty is actually open on them.
///
/// <para>Exists to give the killfeed right of way. A player kill produces a killfeed line <i>and</i> a
/// death line on two independent feeds, and the bridge contract is explicit that the death feed is the
/// unreliable one — "death events are poll-based, lagged, and lossy — use RCON/KillFeed for exact
/// attribution". The killfeed knows who landed the kill; the death feed can only guess from the damage
/// ledger. Resolving the death inline let the guess close the bounty first, after which the killfeed
/// found nothing left to claim and the killer went unpaid. Deferring means the authoritative answer
/// gets to arrive before the guess is made.</para>
/// </summary>
public class ResolveBountyDeathEvent
{
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>
    /// The bounty that was open when they died. Checked again on arrival so a bounty opened during the
    /// grace period is never resolved by a death that happened before it started.
    /// </summary>
    public string QuestInstanceId { get; set; } = string.Empty;
}

/// <summary>
/// A bounty closed — claimed, survived, or cancelled. <see cref="ClaimedByPlayerId"/> is null unless a
/// player killed the target, which includes the case where the bounty completed because the target
/// died to something that was not a player.
/// </summary>
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

/// <summary>
/// A quest instance was fulfilled. The counterpart to <see cref="QuestInstanceExpiredEvent"/>: between
/// them every non-bounty instance ends in exactly one of the two. Bounties report through
/// <see cref="BountyResolvedEvent"/> instead, which carries the target and the mark.
/// </summary>
public class QuestInstanceCompletedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;
    public string QuestId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public QuestType Type { get; set; }
    public string? RegionId { get; set; }
    public string? LocationName { get; set; }

    /// <summary>
    /// The player who fulfilled it: the killer on a hunt, the first to arrive on an exploration. Null
    /// only if the winner could not be resolved back to a registered player.
    /// </summary>
    public string? CompletedByPlayerId { get; set; }

    /// <summary>
    /// Everyone who actually received a reward, best placing first. Deliberately not everyone who
    /// qualified — see <see cref="QuestRewardsGrantedEvent"/> for why the two differ.
    /// </summary>
    public List<string> PaidPlayerIds { get; set; } = [];
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

/// <summary>
/// What a resolved quest actually paid, to whom, in one message.
///
/// <para>Reports granted rewards rather than configured ones: a vital payout to a player who logged
/// off in the last second does not land, and a ledger that claimed otherwise would be wrong. Anything
/// keeping score off this — a website, a payout audit — sees only what really happened.</para>
/// </summary>
public class QuestRewardsGrantedEvent
{
    public string QuestInstanceId { get; set; } = string.Empty;
    public string QuestInstanceFriendlyId { get; set; } = string.Empty;
    public QuestType Type { get; set; }
    public List<QuestRewardGrant> Grants { get; set; } = [];
}
