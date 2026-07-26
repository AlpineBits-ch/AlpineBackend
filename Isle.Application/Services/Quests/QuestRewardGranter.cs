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
/// payouts only ever raise a vital — a reward must never leave a player worse off than it found them.</para>
/// </summary>
public sealed class QuestRewardGranter(
    MicroserviceContext context,
    IBridgeClient bridge,
    ILogger<QuestRewardGranter> logger)
{
    /// <summary>
    /// Grants every reward to one player. Returns the lines to show them. Each reward is independent:
    /// a failed vital write (offline, no pawn) does not stop the XP from landing.
    /// </summary>
    public async Task<IReadOnlyList<string>> GrantAsync(
        string playerId,
        IEnumerable<RewardConfig> rewards,
        CancellationToken ct = default)
    {
        var granted = new List<string>();

        var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
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
