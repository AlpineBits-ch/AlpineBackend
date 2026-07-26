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

    /// <summary>Default payout for putting a marked player down, when the bounty template carries no rewards.</summary>
    public const int DefaultClaimXp = 2500;

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
            "You have been marked. Your skin has changed and the whole server has been told where you are. Survive.", ct);

        await bus.PublishAsync(new PlayerMarkedAsBountyEvent
        {
            QuestInstanceId = instance.Id,
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
    /// A marked player was killed by <paramref name="killerPlayerId"/>. Pays the killer and closes the
    /// bounty. No-op when the victim was not marked.
    /// </summary>
    public async Task<bool> TryClaimAsync(string victimPlayerId, string killerPlayerId, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(victimPlayerId, ct);
        if (instance is null)
            return false;

        if (!instance.TryClose(QuestInstanceState.Completed, killerPlayerId))
            return false;

        await EndAsync(instance, ct);

        var payout = await BuildClaimRewardsAsync(instance, ct);
        var granted = await rewards.GrantAsync(killerPlayerId, payout, ct);

        var killer = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == killerPlayerId, ct);
        var killerName = killer?.InGameName ?? roster.FindBySteam(killer?.SteamId)?.Name ?? "Someone";

        await announcer.AnnounceBountyClaimedAsync(instance, killerName, ct);

        if (killer?.SteamId is { } steam && granted.Count > 0)
            await announcer.WhisperAsync(steam, $"Bounty claimed: {string.Join(", ", granted)}.", ct);

        logger.LogInformation("Bounty {InstanceId} claimed by {KillerId}", instance.Id, killerPlayerId);
        return true;
    }

    /// <summary>
    /// Ends any open bounty on a player without paying anyone — they died to the environment, logged
    /// off, or an admin called it off.
    /// </summary>
    public async Task<bool> CancelForPlayerAsync(string playerId, QuestInstanceState state, CancellationToken ct = default)
    {
        var instance = await FindOpenBountyAsync(playerId, ct);
        if (instance is null)
            return false;

        if (!instance.TryClose(state))
            return false;

        await EndAsync(instance, ct);
        await announcer.AnnounceBountyExpiredAsync(instance, ct);

        logger.LogInformation("Bounty {InstanceId} on {PlayerId} closed as {State}", instance.Id, playerId, state);
        return true;
    }

    /// <summary>Closes bounties whose window ran out. Driven by the quest director tick.</summary>
    public async Task<int> ExpireDueBountiesAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await context.QuestInstances
            .Where(i => i.State == QuestInstanceState.Active && i.Type == QuestType.Bounty && i.ExpiresAt <= now)
            .ToListAsync(ct);

        foreach (var instance in due)
        {
            if (!instance.TryClose(QuestInstanceState.Expired))
                continue;

            await EndAsync(instance, ct);
            await announcer.AnnounceBountyExpiredAsync(instance, ct);
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
    /// Shared teardown: persist the closed state, drop the mark, restore the player's own skin and
    /// clear their streak so they do not immediately re-trigger on the kills that got them marked.
    /// Assumes the caller already won the <see cref="QuestInstance.TryClose"/> race.
    /// </summary>
    private async Task EndAsync(QuestInstance instance, CancellationToken ct)
    {
        await context.SaveChangesAsync(ct);

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
            TargetPlayerId = targetId,
            ClaimedByPlayerId = instance.CompletedByPlayerId,
            State = instance.State,
        });
    }

    /// <summary>
    /// Payout for the claimer: whatever the template carries, plus any admin bonus. Falls back to a
    /// default XP grant so a template authored without rewards still pays something.
    /// </summary>
    private async Task<IReadOnlyList<RewardConfig>> BuildClaimRewardsAsync(QuestInstance instance, CancellationToken ct)
    {
        var template = await context.Quests
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == instance.QuestId, ct);

        var payout = template?.Rewards.ToList() ?? [];

        if (payout.Count == 0)
            payout.Add(new RewardConfig { RewardType = RewardType.Xp, Amount = DefaultClaimXp, AppliesTo = RankRequirement.Winner });

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
