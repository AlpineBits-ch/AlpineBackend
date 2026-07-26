using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Rewards;

/// <summary>Pays out <see cref="RewardConfig"/> rows to a player.</summary>
public sealed class RewardGranter(
    MicroserviceContext context,
    IBridgeClient bridge,
    ILogger<RewardGranter> logger)
{
    /// <summary>Growth is a 0..1 scale; a boost never pushes past fully grown.</summary>
    private const double MaxGrowth = 1.0;

    /// <summary>
    /// <c>getstats</c> reports nutrient levels but no maxima the way it does for hunger or thirst, so
    /// the contract's 0..100 scale is the only ceiling on offer — the same one <c>setstats</c> documents,
    /// where <c>carb: 100</c> is a full bar.
    /// </summary>
    private const double NutrientMax = 100.0;

    /// <summary>
    /// Which reward rows a player who finished at <paramref name="rank"/> collects.
    /// </summary>
    public static bool Applies(RewardConfig reward, RankRequirement rank) => reward.AppliesTo switch
    {
        RankRequirement.AllParticipants => true,
        RankRequirement.Top3 => rank is RankRequirement.Top3 or RankRequirement.Winner,
        RankRequirement.Winner => rank is RankRequirement.Winner,
        _ => false,
    };

    /// <summary>Grants the rewards one player earned at <paramref name="rank"/>.</summary>
    public Task<IReadOnlyList<string>> GrantAsync(
        string playerId,
        IEnumerable<RewardConfig> rewards,
        RankRequirement rank,
        CancellationToken ct = default) =>
        GrantAsync(playerId, rewards.Where(r => Applies(r, rank)), ct);

    /// <summary>Grants an already-selected set of rewards, with no rank filtering.</summary>
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
            logger.LogWarning("Cannot grant rewards: player {PlayerId} not found", playerId);
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
                logger.LogWarning(ex, "Reward {RewardType} failed for player {PlayerId}",
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

    /// <summary>
    /// Hunger, food and the three macronutrients (α carbohydrates, β protein, γ lipids) to
    /// <paramref name="fraction"/> of their maxima.
    /// </summary>
    private async Task<bool> SetDietAsync(string steam, double fraction, CancellationToken ct)
    {
        var snapshot = await ReadSnapshotAsync(steam, ct);
        if (snapshot is null) return false;

        var fed = false;

        // |= rather than || throughout: every channel is written, a refusal never skips the next one.
        if (snapshot.Vitals is { } vitals)
        {
            fed |= await RaiseVitalAsync(steam, VitalName.Hunger, vitals.Hunger, vitals.HungerMax, fraction, ct);
            fed |= await RaiseVitalAsync(steam, VitalName.Food, vitals.Food, vitals.FoodMax, fraction, ct);
        }
        else
        {
            logger.LogDebug("No vitals for {Steam}; feeding nutrients only", steam);
        }

        if (snapshot.Nutrients is { } nutrients)
        {
            foreach (var (name, current) in Macronutrients(nutrients))
                fed |= await RaiseVitalAsync(steam, name, current, NutrientMax, fraction, ct);
        }
        else
        {
            logger.LogDebug("No nutrients for {Steam}; feeding hunger and food only", steam);
        }

        return fed;
    }

    /// <summary>The macronutrient channels a diet reward tops up, paired with their current levels.</summary>
    private static (string Name, double Current)[] Macronutrients(Nutrients n) =>
    [
        (NutrientName.Carb, n.Carb),
        (NutrientName.Protein, n.Protein),
        (NutrientName.Lipid, n.Lipid),
    ];

    private async Task<bool> SetThirstAsync(string steam, double fraction, CancellationToken ct)
    {
        var stats = await ReadVitalsAsync(steam, ct);
        if (stats is null) return false;

        return await RaiseVitalAsync(steam, VitalName.Thirst, stats.Thirst, stats.ThirstMax, fraction, ct);
    }

    /// <summary>One vital to its maximum.</summary>
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
    /// Adds <paramref name="percentagePoints"/> of growth on top of whatever the player currently
    /// has.
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

    /// <summary>Widens the player's dino storage.</summary>
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
    /// Writes <paramref name="name"/> — a vital or a nutrient, both of which <c>setvital</c> takes
    /// — up to <paramref name="fraction"/> of <paramref name="max"/>, skipping the write when the
    /// player is already at or above that.
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
        var snapshot = await ReadSnapshotAsync(steam, ct);
        if (snapshot is null) return null;

        if (snapshot.Vitals is null)
            logger.LogDebug("No vitals for {Steam}; skipping vital reward", steam);

        return snapshot.Vitals;
    }

    /// <summary>
    /// One read for the whole payout — the diet reward needs vitals and nutrients out of the same
    /// snapshot, and a second <c>getstats</c> would only invite the two halves to disagree.
    /// </summary>
    private async Task<StatsSnapshot?> ReadSnapshotAsync(string steam, CancellationToken ct)
    {
        try
        {
            return await bridge.GetStatsAsync(steam, ct);
        }
        catch (Exception ex)
        {
            // Offline or no live pawn — normal, not an error worth escalating.
            logger.LogDebug(ex, "Could not read stats for {Steam}; skipping vital reward", steam);
            return null;
        }
    }
}
