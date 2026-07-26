using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Quests;

/// <summary>
/// Pays out quest rewards.
///
/// <para>Does not go through <c>IRewardFactory</c>/<c>IReward</c>: those are unimplemented stubs whose
/// <c>GrantAsync(Guid playerId)</c> signature does not match this service's string ksuid ids, and they
/// are shaped around game-mode standings rather than a single recipient. Reworking them is a
/// game-mode concern; when that happens this granter is the behaviour to lift.</para>
///
/// <para>Vital payouts read the player's live maxima before writing, because the engine's scales are
/// not guaranteed to be 0..1 across every channel and a hardcoded "full" would be a guess. Half
/// payouts only ever raise a vital — a reward must never leave a player worse off than it found them.
/// The same rule governs growth, which is why it is read before it is written: the bridge exposes only
/// an absolute setter, and a blind write would demote a nearly-grown dino as readily as it promotes a
/// juvenile.</para>
///
/// <para>Everything except XP and the storage slot needs a live pawn, so a payout to a player who has
/// just logged off is skipped and logged rather than queued. XP and slots are database state and land
/// either way — which is the point of having them in the mix.</para>
/// </summary>
public sealed class QuestRewardGranter(
    MicroserviceContext context,
    IBridgeClient bridge,
    ILogger<QuestRewardGranter> logger)
{
    /// <summary>Growth is a 0..1 scale; a boost never pushes past fully grown.</summary>
    private const double MaxGrowth = 1.0;

    /// <summary>
    /// Which reward rows a player who finished at <paramref name="rank"/> collects.
    ///
    /// <para>The tiers nest downwards: the winner is also in the top three and is also a participant,
    /// so they take every row a lesser tier would have taken. Authoring a template the other way round
    /// — expecting <c>Winner</c> and <c>AllParticipants</c> rows to be mutually exclusive — would mean
    /// every template had to restate the participation payout inside the winner payout.</para>
    /// </summary>
    public static bool Applies(RewardConfig reward, RankRequirement rank) => reward.AppliesTo switch
    {
        RankRequirement.AllParticipants => true,
        RankRequirement.Top3 => rank is RankRequirement.Top3 or RankRequirement.Winner,
        RankRequirement.Winner => rank is RankRequirement.Winner,
        _ => false,
    };

    /// <summary>
    /// Grants the rewards one player earned at <paramref name="rank"/>. Returns the lines to show them.
    /// Each reward is independent: a failed vital write (offline, no pawn) does not stop the XP from
    /// landing.
    /// </summary>
    public Task<IReadOnlyList<string>> GrantAsync(
        string playerId,
        IEnumerable<RewardConfig> rewards,
        RankRequirement rank,
        CancellationToken ct = default) =>
        GrantAsync(playerId, rewards.Where(r => Applies(r, rank)), ct);

    /// <summary>
    /// Grants an already-selected set of rewards, with no rank filtering. Callers that know exactly
    /// what they owe use this; callers holding a whole template's reward list want the overload that
    /// takes a <see cref="RankRequirement"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GrantAsync(
        string playerId,
        IEnumerable<RewardConfig> rewards,
        CancellationToken ct = default)
    {
        var granted = new List<string>();

        // Storage comes along because the slot reward mutates it; the rest of the payouts ignore it.
        var player = await context.Players
            .Include(p => p.Storage)
            .FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player is null)
        {
            logger.LogWarning("Cannot grant quest rewards: player {PlayerId} not found", playerId);
            return granted;
        }

        foreach (var reward in rewards)
        {
            try
            {
                var line = await GrantOneAsync(player, reward, ct);
                if (line is not null)
                    granted.Add(line);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Quest reward {RewardType} failed for player {PlayerId}",
                    reward.RewardType, playerId);
            }
        }

        await context.SaveChangesAsync(ct);
        return granted;
    }

    private async Task<string?> GrantOneAsync(Player player, RewardConfig reward, CancellationToken ct)
    {
        switch (reward.RewardType)
        {
            case RewardType.Xp:
                player.AddXp(reward.Amount);
                return $"{reward.Amount:N0} XP";

            case RewardType.FullDiet:
                return await SetDietAsync(player.SteamId, 1.0, ct) ? "a full belly" : null;

            case RewardType.HalfDiet:
                return await SetDietAsync(player.SteamId, 0.5, ct) ? "a half-full belly" : null;

            case RewardType.FullWater:
                return await SetThirstAsync(player.SteamId, 1.0, ct) ? "full water" : null;

            case RewardType.HalfWater:
                return await SetThirstAsync(player.SteamId, 0.5, ct) ? "half-full water" : null;

            case RewardType.FullHealth:
                return await FillVitalAsync(player.SteamId, VitalName.Health, ct) ? "full health" : null;

            case RewardType.FullStamina:
                return await FillVitalAsync(player.SteamId, VitalName.Stamina, ct) ? "full stamina" : null;

            case RewardType.GrowthBoost:
                return await GrowAsync(player.SteamId, reward.Amount, ct);

            case RewardType.StorageSlot:
                return GrantStorageSlots(player, reward.Amount);

            case RewardType.CosmeticUnlock:
                // No cosmetic unlock store exists yet; log it rather than silently dropping the payout.
                logger.LogInformation("Cosmetic reward {CosmeticId} for {PlayerId} is not implemented yet",
                    reward.CosmeticId, player.Id);
                return null;

            default:
                logger.LogWarning("Unknown reward type {RewardType}", reward.RewardType);
                return null;
        }
    }

    /// <summary>Hunger + food to <paramref name="fraction"/> of their maxima. Both channels or nothing.</summary>
    private async Task<bool> SetDietAsync(string steam, double fraction, CancellationToken ct)
    {
        var stats = await ReadVitalsAsync(steam, ct);
        if (stats is null) return false;

        var hunger = await RaiseVitalAsync(steam, VitalName.Hunger, stats.Hunger, stats.HungerMax, fraction, ct);
        var food = await RaiseVitalAsync(steam, VitalName.Food, stats.Food, stats.FoodMax, fraction, ct);

        return hunger || food;
    }

    private async Task<bool> SetThirstAsync(string steam, double fraction, CancellationToken ct)
    {
        var stats = await ReadVitalsAsync(steam, ct);
        if (stats is null) return false;

        return await RaiseVitalAsync(steam, VitalName.Thirst, stats.Thirst, stats.ThirstMax, fraction, ct);
    }

    /// <summary>
    /// One vital to its maximum. Health goes through here rather than the bridge's <c>heal</c> verb so
    /// it obeys the same never-lower rule as every other payout and reports failure the same way.
    /// </summary>
    private async Task<bool> FillVitalAsync(string steam, string name, CancellationToken ct)
    {
        var stats = await ReadVitalsAsync(steam, ct);
        if (stats is null) return false;

        var (current, max) = name switch
        {
            VitalName.Health => (stats.Hp, stats.HpMax),
            VitalName.Stamina => (stats.Stamina, stats.StaminaMax),
            _ => (0d, 0d),
        };

        return await RaiseVitalAsync(steam, name, current, max, 1.0, ct);
    }

    /// <summary>
    /// Adds <paramref name="percentagePoints"/> of growth on top of whatever the player currently has.
    /// Returns null — no payout line — when they are already fully grown, rather than claiming to have
    /// given them something.
    /// </summary>
    private async Task<string?> GrowAsync(string steam, int percentagePoints, CancellationToken ct)
    {
        if (percentagePoints <= 0)
        {
            logger.LogWarning("Growth reward for {Steam} has a non-positive amount; skipping", steam);
            return null;
        }

        double growth;
        try
        {
            growth = (await bridge.GetStatsAsync(steam, ct)).Growth;
        }
        catch (Exception ex)
        {
            // Offline or no live pawn — same normal case as the vitals read.
            logger.LogDebug(ex, "Could not read growth for {Steam}; skipping growth reward", steam);
            return null;
        }

        var target = Math.Min(growth + percentagePoints / 100.0, MaxGrowth);
        if (target <= growth)
            return null;

        var result = await bridge.SetGrowthAsync(steam, target, ct);
        if (result.Ok)
            return $"+{(target - growth) * 100:F0}% growth";

        logger.LogWarning("Setting growth for {Steam} returned {Code}", steam, result.CodeRaw);
        return null;
    }

    /// <summary>
    /// Widens the player's dino storage. Pure database state, so unlike the vital payouts this lands
    /// whether or not the player is online — it is the reward that is still there tomorrow.
    /// </summary>
    private string? GrantStorageSlots(Player player, int amount)
    {
        if (player.Storage is null)
        {
            logger.LogWarning("Cannot grant a storage slot to {PlayerId}: no storage row", player.Id);
            return null;
        }

        // An authored zero means "a slot", not "nothing" — a reward row that pays nothing is a typo.
        var slots = Math.Max(1, amount);

        for (var i = 0; i < slots; i++)
            player.Storage.PurchaseSlot();

        return slots == 1 ? "a storage slot" : $"{slots} storage slots";
    }

    /// <summary>
    /// Writes <paramref name="name"/> up to <paramref name="fraction"/> of <paramref name="max"/>,
    /// skipping the write when the player is already at or above that. Returns whether the vital ended
    /// up at the target (so "already full" still counts as paid).
    /// </summary>
    private async Task<bool> RaiseVitalAsync(
        string steam, string name, double current, double max, double fraction, CancellationToken ct)
    {
        if (max <= 0)
        {
            logger.LogDebug("Skipping {Vital} reward for {Steam}: server reported no maximum", name, steam);
            return false;
        }

        var target = max * fraction;
        if (current >= target)
            return true;

        var result = await bridge.SetVitalAsync(steam, name, target, ct);
        if (result.Ok)
            return true;

        logger.LogWarning("Setting {Vital} for {Steam} returned {Code}", name, steam, result.CodeRaw);
        return false;
    }

    private async Task<Vitals?> ReadVitalsAsync(string steam, CancellationToken ct)
    {
        try
        {
            var snapshot = await bridge.GetStatsAsync(steam, ct);
            if (snapshot.Vitals is null)
                logger.LogDebug("No vitals for {Steam}; skipping vital reward", steam);

            return snapshot.Vitals;
        }
        catch (Exception ex)
        {
            // Offline or no live pawn — normal, not an error worth escalating.
            logger.LogDebug(ex, "Could not read vitals for {Steam}; skipping vital reward", steam);
            return null;
        }
    }
}
