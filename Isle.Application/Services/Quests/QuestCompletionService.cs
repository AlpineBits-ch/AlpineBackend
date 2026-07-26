using Isle.Api.Services.Rewards;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Services.Quests;

/// <summary>How an <c>Exploration</c> or <c>Hunt</c> quest is satisfied, paid and closed.</summary>
public sealed class QuestCompletionService(
    MicroserviceContext context,
    QuestProgressLedger ledger,
    RewardGranter rewards,
    QuestAnnouncer announcer,
    WorldRosterCache roster,
    IMessageBus bus,
    ILogger<QuestCompletionService> logger)
{
    /// <summary>A qualified visitor resolved to a player, with the tier their showing earns.</summary>
    private sealed record RankedVisitor(Player Player, int Ticks, RankRequirement Rank);

    /// <summary>What a payout actually did, as opposed to what it was owed.</summary>
    /// <param name="WinnerLines">The winner's payout lines, for the hunt claim whisper.</param>
    /// <param name="PaidPlayerIds">
    /// Exactly the players something reached, in the order they were paid.
    /// </param>
    private sealed record Payout(IReadOnlyList<string> WinnerLines, IReadOnlyList<string> PaidPlayerIds)
    {
        public static readonly Payout Nothing = new([], []);

        public int PaidCount => PaidPlayerIds.Count;
    }

    /// <summary>
    /// Roster samples inside the region before a player counts as having been there.
    /// </summary>
    public const int RequiredPresenceTicks = 2;

    /// <summary>Qualified visitors, after the first, who still place above the participation tier.</summary>
    private const int TopTierSize = 3;

    /// <summary>Fallback payout for an exploration template authored without rewards.</summary>
    private static readonly RewardConfig[] DefaultExplorationRewards =
    [
        new() { RewardType = RewardType.Xp, Amount = 750, AppliesTo = RankRequirement.AllParticipants },
        new() { RewardType = RewardType.HalfDiet, AppliesTo = RankRequirement.AllParticipants },
        new() { RewardType = RewardType.HalfWater, AppliesTo = RankRequirement.AllParticipants },
    ];

    /// <summary>Fallback payout for a hunt template authored without rewards.</summary>
    private static readonly RewardConfig[] DefaultHuntRewards =
    [
        new() { RewardType = RewardType.Xp, Amount = 2000, AppliesTo = RankRequirement.Winner },
        new() { RewardType = RewardType.FullDiet, AppliesTo = RankRequirement.Winner },
        new() { RewardType = RewardType.FullHealth, AppliesTo = RankRequirement.Winner },
    ];

    /// <summary>How a qualified explorer's payout whisper opens.</summary>
    private const string ExplorationLead = "You made the trip";

    /// <summary>
    /// Unused on the seeded hunt, whose only tier is the winner and whose whisper goes out with the
    /// claim instead.
    /// </summary>
    private const string HuntLead = "You were in on the hunt";

    // --- Presence -----------------------------------------------------------------------------------

    /// <summary>
    /// Credits one roster sample to every player standing in an open quest's region.
    /// </summary>
    /// <returns>How many player-samples were credited, across all open quests.</returns>
    public async Task<int> TrackPresenceAsync(CancellationToken ct = default)
    {
        var open = await OpenTrackableQuestsAsync(ct);
        if (open.Count == 0)
            return 0;

        // Grouped once and reused: on a full server this is a few hundred entries against a handful of
        // quests, and scanning the roster per quest would be the only expensive thing in the tick.
        var byRegion = roster.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.RegionId) && !string.IsNullOrWhiteSpace(e.Steam))
            .GroupBy(e => e.RegionId!)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Steam).ToList());

        if (byRegion.Count == 0)
            return 0;

        var credited = 0;

        foreach (var instance in open)
        {
            if (instance.RegionId is not { } regionId || !byRegion.TryGetValue(regionId, out var present))
                continue;

            await ledger.CreditPresenceAsync(instance.Id, present);
            credited += present.Count;
        }

        return credited;
    }

    /// <summary>Open non-bounty instances that name a region. One without a region can never be matched against.</summary>
    private Task<List<QuestInstance>> OpenTrackableQuestsAsync(CancellationToken ct) =>
        context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active
                        && i.Type != QuestType.Bounty
                        && i.RegionId != null)
            .ToListAsync(ct);

    // --- Hunt ---------------------------------------------------------------------------------------

    /// <summary>A player made a kill.</summary>
    public async Task<bool> TryCompleteHuntAsync(string killerPlayerId, CancellationToken ct = default)
    {
        var open = await context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active && i.Type == QuestType.Hunt && i.RegionId != null)
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync(ct);

        if (open.Count == 0)
            return false;

        var killer = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == killerPlayerId, ct);
        if (killer is null)
            return false;

        if (roster.FindBySteam(killer.SteamId)?.RegionId is not { } regionId)
        {
            logger.LogDebug("{KillerId} made a kill with {Open} hunt(s) open, but the roster does not place " +
                            "them in a region; no hunt claimed", killerPlayerId, open.Count);
            return false;
        }

        // Soonest to expire first, so a kill that could satisfy two overlapping hunts settles the one
        // with the least time left to be satisfied by anything else.
        var instance = open.FirstOrDefault(i => i.RegionId == regionId);
        if (instance is null)
            return false;

        if (!await context.TryCloseQuestAtomicallyAsync(instance, QuestInstanceState.Completed, killerPlayerId, ct))
        {
            // The expiry sweep got there first — the kill landed within a tick of the clock running out.
            logger.LogInformation("Hunt {InstanceId} was already closed when {KillerId}'s kill arrived; not paid",
                instance.Id, killerPlayerId);
            return false;
        }

        var winner = new RankedVisitor(killer, Ticks: 0, RankRequirement.Winner);
        var payout = await PayOutAsync(instance, [winner], HuntLead, ct);

        await FinishAsync(instance, payout, ct);

        var killerName = killer.InGameName ?? roster.FindBySteam(killer.SteamId)?.Name ?? "Someone";
        await announcer.AnnounceQuestCompletedAsync(instance, killerName, payout.PaidCount, ct);

        if (!string.IsNullOrWhiteSpace(killer.SteamId) && payout.WinnerLines.Count > 0)
            await announcer.WhisperAsync(killer.SteamId,
                $"Hunt claimed: {string.Join(", ", payout.WinnerLines)}.", ct);

        logger.LogInformation("Hunt {InstanceId} in {RegionId} claimed by {KillerId}, {Paid} paid",
            instance.Id, regionId, killerPlayerId, payout.PaidCount);

        return true;
    }

    // --- Expiry -------------------------------------------------------------------------------------

    /// <summary>Settles every non-bounty instance whose window has run out.</summary>
    /// <returns>How many instances were settled, completed and expired together.</returns>
    public async Task<int> ResolveDueQuestsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active && i.Type != QuestType.Bounty && i.ExpiresAt <= now)
            .ToListAsync(ct);

        var settled = 0;

        foreach (var instance in due)
        {
            // Read the ledger before anything closes the instance: the teardown drops it.
            var qualified = instance.Type == QuestType.Exploration
                ? await RankAsync(instance, ct)
                : [];

            if (qualified.Count == 0)
            {
                if (await ExpireAsync(instance, ct))
                    settled++;
                continue;
            }

            if (!await context.TryCloseQuestAtomicallyAsync(
                    instance, QuestInstanceState.Completed, qualified[0].Player.Id, ct))
                continue;

            var payout = await PayOutAsync(instance, qualified, ExplorationLead, ct);

            await FinishAsync(instance, payout, ct);

            // No winner named: an exploration is a crowd who all did the same thing, and singling out
            // whoever the roster happened to sample first would read as a placing that was never
            // contested. The first-arrival tier still exists in the payout for templates that author one.
            await announcer.AnnounceQuestCompletedAsync(instance, winnerName: null, payout.PaidCount, ct);

            logger.LogInformation("Quest {InstanceId} ({Title}) completed by {Qualified} visitor(s), {Paid} paid",
                instance.Id, instance.Title, qualified.Count, payout.PaidCount);

            settled++;
        }

        return settled;
    }

    /// <summary>Closes an instance nobody satisfied and keeps the long-standing unclaimed broadcast.</summary>
    private async Task<bool> ExpireAsync(QuestInstance instance, CancellationToken ct)
    {
        if (!await context.TryCloseQuestAtomicallyAsync(
                instance, QuestInstanceState.Expired, completedByPlayerId: null, ct))
            return false;

        await context.SaveChangesAsync(ct);
        await ledger.ClearAsync(instance.Id);

        await announcer.AnnounceQuestExpiredAsync(instance, ct);

        await bus.PublishAsync(new QuestInstanceExpiredEvent
        {
            QuestInstanceId = instance.Id,
            QuestInstanceFriendlyId = instance.FriendlyId,
            QuestId = instance.QuestId,
            Title = instance.Title,
            Type = instance.Type,
            RegionId = instance.RegionId,
            LocationName = instance.LocationName,
            ExpiresAt = instance.ExpiresAt,
        });

        return true;
    }

    // --- Shared teardown and payout -----------------------------------------------------------------

    /// <summary>
    /// Shared teardown for a completed instance: persist, drop the ledger, publish.
    /// </summary>
    private async Task FinishAsync(QuestInstance instance, Payout payout, CancellationToken ct)
    {
        await context.SaveChangesAsync(ct);

        await ledger.ClearAsync(instance.Id);

        await bus.PublishAsync(new QuestInstanceCompletedEvent
        {
            QuestInstanceId = instance.Id,
            QuestInstanceFriendlyId = instance.FriendlyId,
            QuestId = instance.QuestId,
            Title = instance.Title,
            Type = instance.Type,
            RegionId = instance.RegionId,
            LocationName = instance.LocationName,
            CompletedByPlayerId = instance.CompletedByPlayerId,

            // Reports who was paid rather than who qualified, matching the count the lobby was told.
            PaidPlayerIds = payout.PaidPlayerIds.ToList(),
        });
    }

    /// <summary>
    /// Resolves the ledger's Steam ids to players and gives each the tier their showing earns:
    /// earliest arrival first, then <see cref="TopTierSize"/> deep at <see
    /// cref="RankRequirement.Top3"/>, everyone else at <see
    /// cref="RankRequirement.AllParticipants"/>.
    /// </summary>
    private async Task<IReadOnlyList<RankedVisitor>> RankAsync(QuestInstance instance, CancellationToken ct)
    {
        var present = await ledger.GetQualifiedAsync(instance.Id, RequiredPresenceTicks);
        if (present.Count == 0)
            return [];

        var steamIds = present.Select(p => p.SteamId).ToList();

        // Grouped rather than keyed straight into a dictionary, for the reason BountyService.RankAsync
        // documents: nothing constrains steam_id to be unique, and a duplicate would throw here — after
        // the quest had already been closed, so it would end having paid nobody at all.
        var bySteam = (await context.Players
                .AsNoTracking()
                .Where(p => steamIds.Contains(p.SteamId))
                .ToListAsync(ct))
            .GroupBy(p => p.SteamId)
            .ToDictionary(g => g.Key, g => g.First());

        var ranked = new List<RankedVisitor>();
        var placing = 0;

        foreach (var visitor in present)
        {
            if (!bySteam.TryGetValue(visitor.SteamId, out var player))
            {
                logger.LogDebug("Quest visitor {Steam} is not a registered player; no payout", visitor.SteamId);
                continue;
            }

            placing++;

            var rank = placing == 1
                ? RankRequirement.Winner
                : placing <= TopTierSize
                    ? RankRequirement.Top3
                    : RankRequirement.AllParticipants;

            ranked.Add(new RankedVisitor(player, visitor.Ticks, rank));
        }

        return ranked;
    }

    /// <summary>
    /// Pays everyone the resolved quest owes and whispers each of them what they got — nothing else
    /// ever would, since the broadcast is written for the lobby.
    /// </summary>
    private async Task<Payout> PayOutAsync(
        QuestInstance instance,
        IReadOnlyList<RankedVisitor> ranked,
        string participantLead,
        CancellationToken ct)
    {
        if (ranked.Count == 0)
            return Payout.Nothing;

        var rewardTable = await BuildRewardsAsync(instance, ct);

        var winnerLines = new List<string>();
        var grants = new List<QuestRewardGrant>();

        foreach (var visitor in ranked)
        {
            var granted = await rewards.GrantAsync(visitor.Player.Id, rewardTable, visitor.Rank, ct);
            if (granted.Count == 0)
            {
                // Qualified but paid nothing — almost always the granter finding no live pawn, because
                // the player logged off or died between their last sample and the clock running out.
                // They are not counted as paid, and they get no whisper claiming they were.
                logger.LogInformation("Quest {InstanceId}: {PlayerId} qualified at {Rank} but received nothing",
                    instance.Id, visitor.Player.Id, visitor.Rank);
                continue;
            }

            grants.Add(new QuestRewardGrant
            {
                PlayerId = visitor.Player.Id,
                Rank = visitor.Rank,
                Rewards = granted.ToList(),
            });

            if (visitor.Rank == RankRequirement.Winner && instance.Type == QuestType.Hunt)
            {
                // The hunt whispers them alongside the claim announcement, so no second message.
                winnerLines.AddRange(granted);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(visitor.Player.SteamId))
                await announcer.WhisperAsync(visitor.Player.SteamId,
                    $"{participantLead}: {string.Join(", ", granted)}.", ct);
        }

        if (grants.Count > 0)
            await bus.PublishAsync(new QuestRewardsGrantedEvent
            {
                QuestInstanceId = instance.Id,
                QuestInstanceFriendlyId = instance.FriendlyId,
                Type = instance.Type,
                Grants = grants,
            });

        return new Payout(winnerLines, grants.Select(g => g.PlayerId).ToList());
    }

    /// <summary>
    /// The full reward table for a resolved quest — every tier, not one player's share.
    /// </summary>
    private async Task<IReadOnlyList<RewardConfig>> BuildRewardsAsync(QuestInstance instance, CancellationToken ct)
    {
        var template = await context.Quests
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == instance.QuestId, ct);

        var payout = template?.Rewards.ToList() ?? [];

        if (payout.Count == 0)
            payout.AddRange(instance.Type == QuestType.Hunt ? DefaultHuntRewards : DefaultExplorationRewards);

        if (instance.BonusXp is > 0)
            payout.Add(new RewardConfig
            {
                RewardType = RewardType.Xp,
                Amount = instance.BonusXp.Value,

                // Participation tier on an exploration: an admin topping up a quest everyone is paid
                // equally for meant to top up everyone, not whoever the roster sampled first.
                AppliesTo = instance.Type == QuestType.Hunt
                    ? RankRequirement.Winner
                    : RankRequirement.AllParticipants,
            });

        return payout;
    }
}
