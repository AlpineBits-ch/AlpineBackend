using System.Numerics;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Services.Quests;

/// <summary>
/// Marks players who are running away with the server and hands the rest of the lobby a reason to go
/// after them.
///
/// <para>Every terminal path — killed, died some other way, disconnected, timed out, admin-cancelled —
/// funnels into <see cref="EndAsync"/>, and <see cref="QuestInstance.TryClose"/> makes the first one
/// to arrive the winner. That matters because a bounty target being killed produces a killfeed event
/// <i>and</i> a death event, racing each other, while the expiry sweep may be running at the same
/// time; without a single idempotent exit a target could be unmarked twice or rewarded twice.</para>
/// </summary>
public sealed class BountyService(
    MicroserviceContext context,
    BountyRegistry registry,
    BountyParticipantLedger ledger,
    KillStreakTracker streaks,
    QuestAnnouncer announcer,
    QuestRewardGranter rewards,
    WorldRosterCache roster,
    RegionMap regions,
    IBridgeClient bridge,
    ISkinStore skins,
    IMessageBus bus,
    ILogger<BountyService> logger)
{
    /// <summary>A ledger entry resolved to a player, with the tier their contribution earns.</summary>
    private sealed record RankedParticipant(Player Player, double Damage, RankRequirement Rank);

    // --- Spree thresholds ---------------------------------------------------------------------
    // All three gates must pass. The floor alone is not enough: on a quiet server three players
    // trading a kill each are not a spree, and two players neck-and-neck are a fight rather than one
    // person dominating. The lead margin is what encodes "clearly ahead of everyone else".

    /// <summary>Hard floor — nobody is marked below this many kills in the streak window.</summary>
    public const int MinKillsForSpree = 5;

    /// <summary>How far ahead of the runner-up the leader must be before they count as dominant.</summary>
    public const int RequiredLeadOverRunnerUp = 2;

    /// <summary>Below this headcount there is no lobby to rally, so no automatic bounties.</summary>
    public const int MinOnlinePlayersForSpree = 8;

    /// <summary>Automatic bounties running at once. Admin bounties are not capped.</summary>
    public const int MaxConcurrentAutoBounties = 1;

    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(20);

    /// <summary>Default XP for putting a marked player down, when the bounty template carries no rewards.</summary>
    public const int DefaultClaimXp = 2500;

    /// <summary>
    /// Fallback payout, used only when the bounty template was authored without rewards. Killing a
    /// marked player is the hardest thing on offer here, so even the fallback pays in more than one
    /// currency: XP to bank, a full belly and full health so the claimer can keep hunting on the dino
    /// they just risked.
    /// </summary>
    private static readonly RewardConfig[] DefaultClaimRewards =
    [
        new() { RewardType = RewardType.Xp, Amount = DefaultClaimXp, AppliesTo = RankRequirement.Winner },
        new() { RewardType = RewardType.FullDiet, AppliesTo = RankRequirement.Winner },
        new() { RewardType = RewardType.FullHealth, AppliesTo = RankRequirement.Winner },

        // A bare template must not leave the players who wore the target down with nothing — they are
        // the reason the spree ended.
        new() { RewardType = RewardType.Xp, Amount = DefaultParticipationXp, AppliesTo = RankRequirement.AllParticipants },
        new() { RewardType = RewardType.HalfDiet, AppliesTo = RankRequirement.AllParticipants },
    ];

    /// <summary>Fallback XP for having fought the target without landing the kill.</summary>
    public const int DefaultParticipationXp = 500;

    /// <summary>
    /// What running the clock out pays the target.
    ///
    /// <para>Deliberately not XP. Twenty minutes of being hunted leaves a dino hurt, starving and dry,
    /// which is precisely the state that makes the <i>next</i> fight a loss — so the payout for
    /// surviving is being able to carry on playing the dino that survived, rather than a number in the
    /// database.</para>
    ///
    /// <para>Fixed rather than template-authored because <see cref="RankRequirement"/> has no tier
    /// meaning "the target": every tier it does have describes someone who hunted them. These go
    /// through the granter's unranked overload, so <c>AppliesTo</c> is never consulted.</para>
    /// </summary>
    private static readonly RewardConfig[] SurvivalRewards =
    [
        new() { RewardType = RewardType.FullHealth },
        new() { RewardType = RewardType.FullDiet },
        new() { RewardType = RewardType.FullWater },
    ];

    /// <summary>
    /// Damage landed within this long before a death makes it a player's kill.
    ///
    /// <para>A player kill emits a killfeed line and a death line at roughly the same moment and either
    /// can arrive first, so "was this a hunt or an accident" cannot be answered by which event turned
    /// up. The damage ledger answers it without waiting: somebody was hitting the target a second ago,
    /// or nobody was.</para>
    /// </summary>
    public static readonly TimeSpan PvpAttributionWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Called after every kill. Applies the spree gates and marks the killer if they pass. Returns the
    /// instance when a bounty was opened, null in the (overwhelmingly common) case that it was not.
    /// </summary>
    public async Task<QuestInstance?> TryStartFromSpreeAsync(string playerId, int streak, CancellationToken ct = default)
    {
        if (streak < MinKillsForSpree)
            return null;

        if (roster.OnlineCount < MinOnlinePlayersForSpree)
        {
            logger.LogDebug("Spree by {PlayerId} ignored: only {Online} players online", playerId, roster.OnlineCount);
            return null;
        }

        // "Clearly ahead": strictly the top streak, and far enough clear of second place.
        var leaderboard = await streaks.GetLeaderboardAsync();
        var runnerUp = leaderboard.Where(s => s.PlayerId != playerId).Select(s => s.Kills).DefaultIfEmpty(0).Max();

        if (streak - runnerUp < RequiredLeadOverRunnerUp)
        {
            logger.LogDebug("Spree by {PlayerId} ignored: {Streak} kills is not clear of runner-up {RunnerUp}",
                playerId, streak, runnerUp);
            return null;
        }

        var autoBounties = await context.QuestInstances
            .CountAsync(i => i.State == QuestInstanceState.Active && i.Type == QuestType.Bounty && !i.IsAdminSpawned, ct);

        if (autoBounties >= MaxConcurrentAutoBounties)
        {
            logger.LogDebug("Spree by {PlayerId} ignored: an automatic bounty is already running", playerId);
            return null;
        }

        return await StartAsync(playerId, DefaultDuration, bonusXp: null, adminSpawned: false, streak, ct);
    }

    /// <summary>
    /// Opens a bounty on a player, no threshold checks. This is the admin path — see
    /// <c>!questadmin bounty</c>. Returns null when the player cannot be marked (already marked, not
    /// online, or no bounty template exists).
    /// </summary>
    public async Task<QuestInstance?> StartAsync(
        string playerId,
        TimeSpan duration,
        int? bonusXp = null,
        bool adminSpawned = true,
        int? streak = null,
        CancellationToken ct = default)
    {
        var player = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player is null || string.IsNullOrWhiteSpace(player.SteamId))
        {
            logger.LogWarning("Cannot open a bounty on {PlayerId}: unknown player or no steam id", playerId);
            return null;
        }

        var alreadyOpen = await context.QuestInstances
            .AnyAsync(i => i.State == QuestInstanceState.Active
                           && i.Type == QuestType.Bounty
                           && i.TargetPlayerId == playerId, ct);

        if (alreadyOpen)
        {
            logger.LogDebug("Player {PlayerId} already has an open bounty", playerId);
            return null;
        }

        var template = await GetBountyTemplateAsync(ct);
        if (template is null)
        {
            logger.LogWarning("Cannot open a bounty: no quest template of type Bounty is configured");
            return null;
        }

        var (species, position, locationName, regionId) = await LocateAsync(player.SteamId, ct);

        var instance = QuestInstance.Spawn(new SpawnQuestInstanceArgs
        {
            QuestId = template.Id,
            Type = QuestType.Bounty,
            Title = template.Name,
            Duration = duration,
            RegionId = regionId,
            LocationName = locationName,
            WorldX = position?.X,
            WorldY = position?.Y,
            TargetPlayerId = playerId,
            TargetSpecies = species,
            IsAdminSpawned = adminSpawned,
            BonusXp = bonusXp,
        });

        context.QuestInstances.Add(instance);
        template.LastSpawnedAt = instance.StartedAt;
        await context.SaveChangesAsync(ct);

        await registry.MarkAsync(new BountyMark(
            instance.Id, playerId, player.SteamId, species, instance.ExpiresAt));

        await ApplyMarkerSkinAsync(player.SteamId, species, ct);

        logger.LogInformation("Bounty {InstanceId} opened on {PlayerId} ({Species}) at {Location}, admin={Admin}, streak={Streak}",
            instance.Id, playerId, species, locationName, adminSpawned, streak);

        await announcer.AnnounceBountyAsync(instance, ct);
        await announcer.WhisperAsync(player.SteamId,
            "You have been marked. Your colours have changed and the server has been told where you were last seen. " +
            "Survive.", ct);

        await bus.PublishAsync(new PlayerMarkedAsBountyEvent
        {
            QuestInstanceId = instance.Id,
            QuestInstanceFriendlyId = instance.FriendlyId,
            TargetPlayerId = playerId,
            TargetSteamId = player.SteamId,
            TargetSpecies = species,
            KillStreak = streak ?? 0,
            RegionId = regionId,
            LocationName = locationName,
            WorldX = instance.WorldX,
            WorldY = instance.WorldY,
            ExpiresAt = instance.ExpiresAt,
            IsAdminSpawned = adminSpawned,
        });

        return instance;
    }

    /// <summary>
    /// A marked player was killed by <paramref name="killerPlayerId"/>. Closes the bounty, pays the
    /// killer the winner's share and everyone who helped their own. No-op when the victim was not
    /// marked.
    /// </summary>
    public async Task<bool> TryClaimAsync(string victimPlayerId, string killerPlayerId, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(victimPlayerId, ct);
        if (instance is null)
            return false;

        if (!await TryCloseAtomicallyAsync(instance, QuestInstanceState.Completed, killerPlayerId, ct))
            return false;

        // Read the ledger before the teardown drops it.
        var ranked = await RankAsync(await ledger.GetParticipantsAsync(instance.Id), killerPlayerId, ct);

        await EndAsync(instance, ranked, ct);

        var winnerLines = await PayOutAsync(instance, ranked, KilledParticipantLead, ct);

        var killer = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == killerPlayerId, ct);
        var killerName = killer?.InGameName ?? roster.FindBySteam(killer?.SteamId)?.Name ?? "Someone";

        await announcer.AnnounceBountyClaimedAsync(instance, killerName, ct);

        if (killer?.SteamId is { } steam && winnerLines.Count > 0)
            await announcer.WhisperAsync(steam, $"Bounty claimed: {string.Join(", ", winnerLines)}.", ct);

        logger.LogInformation("Bounty {InstanceId} claimed by {KillerId}, {Participants} paid",
            instance.Id, killerPlayerId, ranked.Count);
        return true;
    }

    /// <summary>
    /// The marked player died. Decides whether that was the hunt succeeding or an accident, and
    /// resolves the bounty accordingly. No-op when they were not marked.
    ///
    /// <para>Either way the bounty <i>completes</i> — the target is dead, which is what the server was
    /// asked to make happen. The difference is who gets paid: a player kill pays the killer the
    /// winner's share on top of the participation share, while a drowning, a fall or a starvation pays
    /// only the players who put the damage in. Nobody is credited with a kill they did not make.</para>
    /// </summary>
    public async Task<bool> TryResolveOnDeathAsync(string playerId, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(playerId, ct);
        if (instance is null)
            return false;

        var lastAttacker = await ledger.LastAttackerAsync(instance.Id);
        var killedByPlayer = lastAttacker is not null
                             && DateTimeOffset.UtcNow - lastAttacker.LastHitAt <= PvpAttributionWindow;

        if (killedByPlayer)
        {
            // The claim path owns this. It races the killfeed handler doing the same thing, and
            // QuestInstance.TryClose makes whichever arrives first the only one that pays.
            var killer = await context.Players
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.SteamId == lastAttacker!.SteamId, ct);

            if (killer is not null)
                return await TryClaimAsync(playerId, killer.Id, ct);

            logger.LogDebug("Last attacker {Steam} on bounty {InstanceId} is not a registered player; " +
                            "resolving as a natural death", lastAttacker!.SteamId, instance.Id);
        }

        return await CompleteOnNaturalCausesAsync(instance, ct);
    }

    /// <summary>
    /// The target went down to something that was not a player — drowned, starved, fell, broke a leg.
    /// The bounty still completes, and everyone who wore them down collects the participation rewards.
    /// Nobody collects the winner's share, because there is no winner.
    /// </summary>
    private async Task<bool> CompleteOnNaturalCausesAsync(QuestInstance instance, CancellationToken ct)
    {
        if (!await TryCloseAtomicallyAsync(instance, QuestInstanceState.Completed, completedByPlayerId: null, ct))
            return false;

        var ranked = await RankAsync(await ledger.GetParticipantsAsync(instance.Id), winnerPlayerId: null, ct);

        await EndAsync(instance, ranked, ct);
        await PayOutAsync(instance, ranked, KilledParticipantLead, ct);
        await announcer.AnnounceBountyDiedAsync(instance, ranked.Count, ct);

        logger.LogInformation("Bounty {InstanceId} completed by natural causes, {Participants} paid",
            instance.Id, ranked.Count);
        return true;
    }

    /// <summary>
    /// Ends any open bounty on a player without paying anyone — they logged off, or an admin called it
    /// off. A death is not one of these: see <see cref="TryResolveOnDeathAsync"/>.
    /// </summary>
    public async Task<bool> CancelForPlayerAsync(string playerId, QuestInstanceState state, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(playerId, ct);
        if (instance is null)
            return false;

        if (!await TryCloseAtomicallyAsync(instance, state, completedByPlayerId: null, ct))
            return false;

        await EndAsync(instance, [], ct);

        // Called off rather than survived, so nobody is paid — not the hunters, not the target.
        await announcer.AnnounceBountyExpiredAsync(instance, participants: 0, ct);

        logger.LogInformation("Bounty {InstanceId} on {PlayerId} closed as {State}", instance.Id, playerId, state);
        return true;
    }

    /// <summary>
    /// Closes bounties whose window ran out. Driven by the quest director tick.
    ///
    /// <para>This is the one terminal path where the target won: they were named to the whole server and
    /// were still standing when the clock hit zero, so they are paid for it — see
    /// <see cref="PaySurvivorAsync"/>. The hunters are paid too, at
    /// <see cref="RankRequirement.AllParticipants"/> and no higher: committing to a fight with a marked
    /// player is the behaviour the bounty exists to provoke, and paying nothing for it teaches the lobby
    /// that engaging a target you might not finish is a waste of a dino. What they do not get is the
    /// placing tiers — nobody brought the target down, so nobody placed.</para>
    /// </summary>
    public async Task<int> ExpireDueBountiesAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active && i.Type == QuestType.Bounty && i.ExpiresAt <= now)
            .ToListAsync(ct);

        foreach (var instance in due)
        {
            if (!await TryCloseAtomicallyAsync(instance, QuestInstanceState.Expired, completedByPlayerId: null, ct))
                continue;

            // Read the ledger before the teardown drops it.
            var hunters = await RankAsync(await ledger.GetParticipantsAsync(instance.Id), winnerPlayerId: null, ct);

            // Flattened to the participation tier: the damage ranking decides who placed in a hunt that
            // succeeded, and this one did not.
            var ranked = hunters
                .Select(h => h with { Rank = RankRequirement.AllParticipants })
                .ToList();

            await EndAsync(instance, ranked, ct);
            await PaySurvivorAsync(instance, ct);
            await PayOutAsync(instance, ranked, SurvivedParticipantLead, ct);
            await announcer.AnnounceBountyExpiredAsync(instance, ranked.Count, ct);

            logger.LogInformation("Bounty {InstanceId} expired with the target alive, {Participants} hunters paid",
                instance.Id, ranked.Count);
        }

        return due.Count;
    }

    public Task<QuestInstance?> FindOpenBountyAsync(string playerId, CancellationToken ct = default) =>
        context.QuestInstances
            .FirstOrDefaultAsync(i => i.State == QuestInstanceState.Active
                                      && i.Type == QuestType.Bounty
                                      && i.TargetPlayerId == playerId, ct);

    public Task<List<QuestInstance>> GetOpenBountiesAsync(CancellationToken ct = default) =>
        context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active && i.Type == QuestType.Bounty)
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync(ct);

    /// <summary>
    /// Claims the right to close this bounty, atomically, and returns whether this caller won.
    ///
    /// <para>A player kill produces a killfeed event and a death event, and Wolverine hands them to two
    /// handlers in two scopes with two <c>MicroserviceContext</c>s. <see cref="QuestInstance.TryClose"/>
    /// guards one in-memory instance and cannot see the other scope, so both could pass it — which used
    /// to mean the state was written twice and paid once, and now that both paths pay would mean paid
    /// twice. This is the real gate: a conditional UPDATE that only matches while the row is still
    /// active, so exactly one caller gets a row back and everyone else walks away. Under Postgres read
    /// committed the loser blocks on the row lock, re-evaluates the predicate after the winner commits,
    /// and matches nothing.</para>
    ///
    /// <para>The non-bounty quest paths keep using <see cref="QuestInstance.TryClose"/> directly: their
    /// only writer is the director tick, so there is no second scope to race.</para>
    /// </summary>
    private async Task<bool> TryCloseAtomicallyAsync(
        QuestInstance instance,
        QuestInstanceState state,
        string? completedByPlayerId,
        CancellationToken ct)
    {
        if (state == QuestInstanceState.Active)
            return false;

        var endedAt = DateTimeOffset.UtcNow;

        var rows = await context.QuestInstances
            .Where(i => i.Id == instance.Id && i.State == QuestInstanceState.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.State, state)
                .SetProperty(i => i.CompletedByPlayerId, completedByPlayerId)
                .SetProperty(i => i.EndedAt, endedAt)
                .SetProperty(i => i.UpdatedAt, endedAt), ct);

        if (rows == 0)
            return false;

        // Bring the tracked entity in step with the row: the announcement and the resolved event are
        // both built off it after this returns.
        instance.State = state;
        instance.CompletedByPlayerId = completedByPlayerId;
        instance.EndedAt = endedAt;
        instance.UpdatedAt = endedAt;

        return true;
    }

    /// <summary>
    /// Shared teardown: persist the closed state, drop the mark and the damage ledger, restore the
    /// player's own skin and clear their streak so they do not immediately re-trigger on the kills that
    /// got them marked. Assumes the caller already won the <see cref="QuestInstance.TryClose"/> race,
    /// and that it has already read anything it needs out of the ledger.
    /// </summary>
    private async Task EndAsync(QuestInstance instance, IReadOnlyList<RankedParticipant> ranked, CancellationToken ct)
    {
        await context.SaveChangesAsync(ct);

        await ledger.ClearAsync(instance.Id);

        if (instance.TargetPlayerId is not { } targetId)
            return;

        var target = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetId, ct);

        if (target?.SteamId is { } steam)
        {
            await registry.UnmarkAsync(steam);
            await RestoreSkinAsync(steam, ct);
        }

        await streaks.ResetAsync(targetId);

        await bus.PublishAsync(new BountyResolvedEvent
        {
            QuestInstanceId = instance.Id,
            QuestInstanceFriendlyId = instance.FriendlyId,
            TargetPlayerId = targetId,
            ClaimedByPlayerId = instance.CompletedByPlayerId,
            State = instance.State,
            ParticipantPlayerIds = ranked.Select(r => r.Player.Id).ToList(),
        });
    }

    /// <summary>
    /// Resolves the ledger's Steam ids to players and gives each one the tier their contribution earns:
    /// hardest hitters first, the top <see cref="BountyParticipantLedger.TopTierSize"/> at
    /// <see cref="RankRequirement.Top3"/>, everyone else at <see cref="RankRequirement.AllParticipants"/>.
    ///
    /// <para>The winner outranks all of that regardless of how much damage they personally did — landing
    /// the kill is the thing being paid for — and is included even when the ledger holds nothing for
    /// them, which is what a Redis outage during the bounty looks like.</para>
    /// </summary>
    private async Task<IReadOnlyList<RankedParticipant>> RankAsync(
        IReadOnlyList<BountyParticipation> participants,
        string? winnerPlayerId,
        CancellationToken ct)
    {
        var steamIds = participants.Select(p => p.SteamId).ToList();

        var bySteam = steamIds.Count == 0
            ? new Dictionary<string, Player>()
            : await context.Players
                .AsNoTracking()
                .Where(p => steamIds.Contains(p.SteamId))
                .ToDictionaryAsync(p => p.SteamId, ct);

        var ranked = new List<RankedParticipant>();
        var placing = 0;

        foreach (var participant in participants)
        {
            if (!bySteam.TryGetValue(participant.SteamId, out var player))
            {
                logger.LogDebug("Bounty participant {Steam} is not a registered player; no payout", participant.SteamId);
                continue;
            }

            placing++;

            var rank = player.Id == winnerPlayerId
                ? RankRequirement.Winner
                : placing <= BountyParticipantLedger.TopTierSize
                    ? RankRequirement.Top3
                    : RankRequirement.AllParticipants;

            ranked.Add(new RankedParticipant(player, participant.Damage, rank));
        }

        if (winnerPlayerId is not null && ranked.All(r => r.Player.Id != winnerPlayerId))
        {
            var winner = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == winnerPlayerId, ct);
            if (winner is not null)
                ranked.Insert(0, new RankedParticipant(winner, 0, RankRequirement.Winner));
        }

        return ranked;
    }

    /// <summary>How a hunter's payout whisper opens when the target went down.</summary>
    private const string KilledParticipantLead = "You helped bring the marked player down";

    /// <summary>
    /// How it opens when the target outlasted the clock. Says plainly that they got away, because a
    /// whisper congratulating a hunter on a kill that did not happen reads as a bug.
    /// </summary>
    private const string SurvivedParticipantLead = "The marked player got away, but you made them bleed for it";

    /// <summary>
    /// Pays everyone the resolved bounty owes and tells them what they got. Returns the winner's payout
    /// lines, which the claim announcement uses; participants are whispered here because nothing else
    /// would ever tell them they had been paid.
    /// </summary>
    /// <param name="participantLead">
    /// Opening clause of the participant whisper — the payout is the same on every path, what it means
    /// is not. See <see cref="KilledParticipantLead"/> and <see cref="SurvivedParticipantLead"/>.
    /// </param>
    private async Task<IReadOnlyList<string>> PayOutAsync(
        QuestInstance instance,
        IReadOnlyList<RankedParticipant> ranked,
        string participantLead,
        CancellationToken ct)
    {
        if (ranked.Count == 0)
            return [];

        var payout = await BuildClaimRewardsAsync(instance, ct);

        var winnerLines = new List<string>();
        var grants = new List<QuestRewardGrant>();

        foreach (var participant in ranked)
        {
            var granted = await rewards.GrantAsync(participant.Player.Id, payout, participant.Rank, ct);
            if (granted.Count == 0)
                continue;

            grants.Add(new QuestRewardGrant
            {
                PlayerId = participant.Player.Id,
                Rank = participant.Rank,
                Rewards = granted.ToList(),
            });

            if (participant.Rank == RankRequirement.Winner)
            {
                // The claim path whispers them alongside the announcement, so no second message.
                winnerLines.AddRange(granted);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(participant.Player.SteamId))
                await announcer.WhisperAsync(participant.Player.SteamId,
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

        return winnerLines;
    }

    /// <summary>
    /// Pays the target for outlasting the bounty and tells them they made it — the only message that
    /// ever reaches them about the outcome, since the expiry broadcast is written for the lobby.
    ///
    /// <para>Runs after <see cref="EndAsync"/>, so the mark is out of the registry and the marker skin is
    /// already off them: by the time they are told they are safe, they look it.</para>
    ///
    /// <para>Reported as <see cref="RankRequirement.Winner"/> in the payout event because on this path
    /// they are the winner — nobody else is paid at all.</para>
    /// </summary>
    private async Task PaySurvivorAsync(QuestInstance instance, CancellationToken ct)
    {
        if (instance.TargetPlayerId is not { } targetId)
            return;

        var target = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetId, ct);
        if (target is null)
        {
            logger.LogWarning("Bounty {InstanceId} expired but its target {PlayerId} is unknown; no survival payout",
                instance.Id, targetId);
            return;
        }

        var granted = await rewards.GrantAsync(targetId, SurvivalRewards, ct);

        if (!string.IsNullOrWhiteSpace(target.SteamId))
        {
            // An empty payout means the granter found no live pawn to write to, so the whisper will not
            // land either — but it costs nothing to send, and it must not claim rewards that never
            // happened.
            var body = granted.Count == 0
                ? "You survived the bounty. The mark is gone and your colours are your own again."
                : $"You survived the bounty. The mark is gone, your colours are your own again, and you " +
                  $"have been patched up: {string.Join(", ", granted)}.";

            await announcer.WhisperAsync(target.SteamId, body, ct);
        }

        if (granted.Count == 0)
        {
            logger.LogDebug("Bounty {InstanceId} survived by {PlayerId} but nothing could be granted",
                instance.Id, targetId);
            return;
        }

        await bus.PublishAsync(new QuestRewardsGrantedEvent
        {
            QuestInstanceId = instance.Id,
            QuestInstanceFriendlyId = instance.FriendlyId,
            Type = instance.Type,
            Grants =
            [
                new QuestRewardGrant
                {
                    PlayerId = targetId,
                    Rank = RankRequirement.Winner,
                    Rewards = granted.ToList(),
                },
            ],
        });

        logger.LogInformation("Bounty {InstanceId} survived by {PlayerId}; paid {Rewards}",
            instance.Id, targetId, string.Join(", ", granted));
    }

    /// <summary>
    /// The full reward table for a resolved bounty — every tier, not one player's share. Whatever the
    /// template carries, plus any admin bonus, falling back to <see cref="DefaultClaimRewards"/> so a
    /// template authored without rewards still pays properly. <c>QuestRewardGranter</c> is what narrows
    /// this down per player.
    /// </summary>
    private async Task<IReadOnlyList<RewardConfig>> BuildClaimRewardsAsync(QuestInstance instance, CancellationToken ct)
    {
        var template = await context.Quests
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == instance.QuestId, ct);

        var payout = template?.Rewards.ToList() ?? [];

        if (payout.Count == 0)
            payout.AddRange(DefaultClaimRewards);

        if (instance.BonusXp is > 0)
            payout.Add(new RewardConfig { RewardType = RewardType.Xp, Amount = instance.BonusXp.Value, AppliesTo = RankRequirement.Winner });

        return payout;
    }

    private async Task ApplyMarkerSkinAsync(string steam, string? species, CancellationToken ct)
    {
        try
        {
            var result = await bridge.SetSkinAsync(steam, BountyMarkerSkin.For(species), ct);
            if (!result.Ok)
                logger.LogWarning("Applying bounty skin to {Steam} returned {Code}", steam, result.CodeRaw);
        }
        catch (Exception ex)
        {
            // The bounty stands even if the skin does not — the broadcast has already named them.
            logger.LogWarning(ex, "Could not apply bounty skin to {Steam}", steam);
        }
    }

    /// <summary>
    /// Puts the player back in their own colours. The mark is already gone from the registry at this
    /// point, so <c>SkinStore</c> now reports their real skin. A player who never made one keeps the
    /// pale skin until their next respawn — there is no "clear skin" verb in the bridge contract.
    /// </summary>
    private async Task RestoreSkinAsync(string steam, CancellationToken ct)
    {
        try
        {
            var own = await skins.GetAsync(steam, ct);
            if (own is null)
            {
                logger.LogDebug("No stored skin for {Steam}; marker persists until respawn", steam);
                return;
            }

            await bridge.SetSkinAsync(steam, own, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore skin for {Steam}", steam);
        }
    }

    /// <summary>Species and position for the announcement. Roster snapshot first; a live bridge read is the fallback.</summary>
    private async Task<(string? Species, Vector3? Position, string LocationName, string? RegionId)> LocateAsync(
        string steam, CancellationToken ct)
    {
        if (roster.FindBySteam(steam) is { } entry)
            return (entry.Species, entry.Position, entry.LocationName, entry.RegionId);

        try
        {
            var stats = await bridge.GetStatsAsync(steam, ct);
            if (stats.Pos is { } pos)
            {
                var position = new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z);
                var species = stats.Species is null ? null : Species.FriendlyName(stats.Species);
                return (species, position, regions.Describe(position), regions.Resolve(position)?.Id);
            }

            return (stats.Species is null ? null : Species.FriendlyName(stats.Species), null, "an unknown location", null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not locate bounty target {Steam}", steam);
            return (null, null, "an unknown location", null);
        }
    }

    private async Task<Quest?> GetBountyTemplateAsync(CancellationToken ct) =>
        await context.Quests
            .Include(q => q.Locations)
            .Where(q => q.Type == QuestType.Bounty)
            .OrderByDescending(q => q.Enabled)
            .FirstOrDefaultAsync(ct);
}
