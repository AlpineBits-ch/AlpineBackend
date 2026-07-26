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
/// Marks players who are running away with the server and hands the rest of the lobby a reason to
/// go after them.
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

    /// <summary>What a payout actually did, as opposed to what it was owed.</summary>
    /// <param name="WinnerLines">The winner's payout lines, for the claim whisper.</param>
    /// <param name="PaidCount">How many players actually received at least one reward.</param>
    private sealed record Payout(IReadOnlyList<string> WinnerLines, int PaidCount)
    {
        public static readonly Payout Nothing = new([], 0);
    }

    // --- Spree thresholds ---------------------------------------------------------------------
    // All three gates must pass.

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
    /// Fallback payout, used only when the bounty template was authored without rewards.
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

    /// <summary>What running the clock out pays the target.</summary>
    private static readonly RewardConfig[] SurvivalRewards =
    [
        new() { RewardType = RewardType.FullHealth },
        new() { RewardType = RewardType.FullDiet },
        new() { RewardType = RewardType.FullWater },
    ];

    /// <summary>Damage landed within this long before a death makes it a player's kill.</summary>
    public static readonly TimeSpan PvpAttributionWindow = TimeSpan.FromSeconds(20);

    /// <summary>Called after every kill.</summary>
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

    /// <summary>Opens a bounty on a player, no threshold checks.</summary>
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

    /// <summary>A marked player was killed by <paramref name="killerPlayerId"/>.</summary>
    public async Task<bool> TryClaimAsync(string victimPlayerId, string killerPlayerId, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(victimPlayerId, ct);
        if (instance is null)
        {
            // Nothing to pay out on.
            logger.LogDebug("No open bounty on {VictimId}; nothing for {KillerId} to claim",
                victimPlayerId, killerPlayerId);
            return false;
        }

        if (!await TryCloseAtomicallyAsync(instance, QuestInstanceState.Completed, killerPlayerId, ct))
        {
            logger.LogWarning("Killfeed reached bounty {InstanceId} after it was already closed; " +
                              "{KillerId} will not be paid for the kill", instance.Id, killerPlayerId);
            return false;
        }

        // Read the ledger before the teardown drops it.
        var ranked = await RankAsync(await ledger.GetParticipantsAsync(instance.Id), killerPlayerId, ct);

        await EndAsync(instance, ranked, ct);

        var payout = await PayOutAsync(instance, ranked, KilledParticipantLead, ct);

        var killer = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == killerPlayerId, ct);
        var killerName = killer?.InGameName ?? roster.FindBySteam(killer?.SteamId)?.Name ?? "Someone";

        await announcer.AnnounceBountyClaimedAsync(instance, killerName, ct);

        if (killer?.SteamId is { } steam && payout.WinnerLines.Count > 0)
            await announcer.WhisperAsync(steam, $"Bounty claimed: {string.Join(", ", payout.WinnerLines)}.", ct);

        logger.LogInformation("Bounty {InstanceId} claimed by {KillerId}, {Paid} of {Ranked} paid",
            instance.Id, killerPlayerId, payout.PaidCount, ranked.Count);
        return true;
    }

    /// <summary>The marked player died.</summary>
    public async Task<bool> TryResolveOnDeathAsync(string playerId, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(playerId, ct);
        if (instance is null)
            return false;

        var lastAttacker = await ledger.LastAttackerAsync(instance.Id);
        var sinceLastHit = lastAttacker is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - lastAttacker.LastHitAt;
        var killedByPlayer = sinceLastHit <= PvpAttributionWindow;

        // This is the decision that says whether anyone gets credited with the kill, and it is made off
        // a heuristic, so it says out loud what it decided and on what evidence.
        logger.LogInformation("Resolving death of bounty {InstanceId}: last attacker {Steam}, last hit {Age} ago, " +
                              "attribution window {Window} — treating as {Verdict}",
            instance.Id, lastAttacker?.SteamId ?? "(none)", sinceLastHit, PvpAttributionWindow,
            killedByPlayer ? "a player kill" : "natural causes");

        if (killedByPlayer)
        {
            // The claim path owns this.
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
    /// The target went down to something that was not a player — drowned, starved, fell, broke a
    /// leg.
    /// </summary>
    private async Task<bool> CompleteOnNaturalCausesAsync(QuestInstance instance, CancellationToken ct)
    {
        if (!await TryCloseAtomicallyAsync(instance, QuestInstanceState.Completed, completedByPlayerId: null, ct))
            return false;

        var ranked = await RankAsync(await ledger.GetParticipantsAsync(instance.Id), winnerPlayerId: null, ct);

        await EndAsync(instance, ranked, ct);

        var payout = await PayOutAsync(instance, ranked, KilledParticipantLead, ct);

        await announcer.AnnounceBountyDiedAsync(instance, payout.PaidCount, ct);

        logger.LogInformation("Bounty {InstanceId} completed by natural causes, {Paid} of {Ranked} paid",
            instance.Id, payout.PaidCount, ranked.Count);
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

    /// <summary>Closes bounties whose window ran out.</summary>
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

            var payout = await PayOutAsync(instance, ranked, SurvivedParticipantLead, ct);

            await announcer.AnnounceBountyExpiredAsync(instance, payout.PaidCount, ct);

            logger.LogInformation("Bounty {InstanceId} expired with the target alive, {Paid} of {Ranked} hunters paid",
                instance.Id, payout.PaidCount, ranked.Count);
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
    /// player's own skin and clear their streak so they do not immediately re-trigger on the kills
    /// that got them marked.
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
    /// Resolves the ledger's Steam ids to players and gives each one the tier their contribution
    /// earns: hardest hitters first, the top <see cref="BountyParticipantLedger.TopTierSize"/> at
    /// <see cref="RankRequirement.Top3"/>, everyone else at <see
    /// cref="RankRequirement.AllParticipants"/>.
    /// </summary>
    private async Task<IReadOnlyList<RankedParticipant>> RankAsync(
        IReadOnlyList<BountyParticipation> participants,
        string? winnerPlayerId,
        CancellationToken ct)
    {
        var steamIds = participants.Select(p => p.SteamId).ToList();

        // Grouped rather than keyed straight into a dictionary: nothing constrains steam_id to be unique
        // across players, and a duplicate row would throw here — after the bounty has already been
        // closed, so the hunt would end having paid nobody at all.
        var bySteam = steamIds.Count == 0
            ? new Dictionary<string, Player>()
            : (await context.Players
                    .AsNoTracking()
                    .Where(p => steamIds.Contains(p.SteamId))
                    .ToListAsync(ct))
                .GroupBy(p => p.SteamId)
                .ToDictionary(g => g.Key, g => g.First());

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

    /// <summary>How it opens when the target outlasted the clock.</summary>
    private const string SurvivedParticipantLead = "The marked player got away, but you made them bleed for it";

    /// <summary>Pays everyone the resolved bounty owes and tells them what they got.</summary>
    /// <param name="participantLead">
    /// Opening clause of the participant whisper — the payout is the same on every path, what it
    /// means is not.
    /// </param>
    private async Task<Payout> PayOutAsync(
        QuestInstance instance,
        IReadOnlyList<RankedParticipant> ranked,
        string participantLead,
        CancellationToken ct)
    {
        if (ranked.Count == 0)
            return Payout.Nothing;

        var rewardTable = await BuildClaimRewardsAsync(instance, ct);

        var winnerLines = new List<string>();
        var grants = new List<QuestRewardGrant>();

        foreach (var participant in ranked)
        {
            var granted = await rewards.GrantAsync(participant.Player.Id, rewardTable, participant.Rank, ct);
            if (granted.Count == 0)
            {
                // Ranked but paid nothing.
                logger.LogInformation("Bounty {InstanceId}: {PlayerId} ranked {Rank} but received nothing",
                    instance.Id, participant.Player.Id, participant.Rank);
                continue;
            }

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

        return new Payout(winnerLines, grants.Count);
    }

    /// <summary>
    /// Pays the target for outlasting the bounty and tells them they made it — the only message
    /// that ever reaches them about the outcome, since the expiry broadcast is written for the
    /// lobby.
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
    /// The full reward table for a resolved bounty — every tier, not one player's share.
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

    /// <summary>Puts the player back in their own colours.</summary>
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
