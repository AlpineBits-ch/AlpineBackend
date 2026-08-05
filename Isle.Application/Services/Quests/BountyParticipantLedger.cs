using System.Globalization;
using StackExchange.Redis;

namespace Isle.Api.Services.Quests;

/// <summary>One player's contribution to bringing a marked target down.</summary>
/// <param name="SteamId">The attacker, unresolved - the ledger never touches the database.</param>
/// <param name="Damage">Total damage they dealt to the target over the life of the bounty.</param>
/// <param name="LastHitAt">When they last connected, used to tell a kill from a coincidence.</param>
public sealed record BountyParticipation(string SteamId, double Damage, DateTimeOffset LastHitAt);

/// <summary>Who actually fought the marked player, and how hard.</summary>
public sealed class BountyParticipantLedger(IConnectionMultiplexer redis, ILogger<BountyParticipantLedger> logger)
{
    /// <summary>Below this much total damage a player is a bystander, not a participant.</summary>
    public const double MinDamageForParticipation = 100d;

    /// <summary>How many top damage dealers <see cref="Isle.Domain.Enums.RankRequirement.Top3"/> covers.</summary>
    public const int TopTierSize = 3;

    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(12);

    private IDatabase Db => redis.GetDatabase();

    private static string DamageKey(string questInstanceId) => $"isle:bounty:damage:{questInstanceId}";

    private static string LastHitKey(string questInstanceId) => $"isle:bounty:lasthit:{questInstanceId}";

    /// <summary>Credits <paramref name="attackerSteamId"/> with damage against the bounty target.</summary>
    public async Task RecordAsync(string questInstanceId, string attackerSteamId, double damage, DateTimeOffset at)
    {
        if (damage <= 0)
            return;

        try
        {
            var damageKey = DamageKey(questInstanceId);
            var lastHitKey = LastHitKey(questInstanceId);

            await Db.HashIncrementAsync(damageKey, attackerSteamId, damage);
            await Db.HashSetAsync(lastHitKey, attackerSteamId, at.ToUnixTimeMilliseconds());

            await Db.KeyExpireAsync(damageKey, KeyTtl);
            await Db.KeyExpireAsync(lastHitKey, KeyTtl);
        }
        catch (Exception ex)
        {
            // A missed hit costs one player some credit; it must never abort the damage handler.
            logger.LogWarning(ex, "Could not record bounty damage by {Steam} on {InstanceId}",
                attackerSteamId, questInstanceId);
        }
    }

    /// <summary>
    /// Everyone who cleared <see cref="MinDamageForParticipation"/>, hardest hitter first.
    /// </summary>
    public async Task<IReadOnlyList<BountyParticipation>> GetParticipantsAsync(string questInstanceId)
    {
        try
        {
            var damage = await Db.HashGetAllAsync(DamageKey(questInstanceId));
            if (damage.Length == 0)
                return [];

            var lastHits = (await Db.HashGetAllAsync(LastHitKey(questInstanceId)))
                .ToDictionary(e => e.Name.ToString(), e => (long)e.Value);

            var attackers = damage
                .Select(entry =>
                {
                    var steam = entry.Name.ToString();
                    if (!TryParseDamage(entry.Value, out var dealt))
                        return null;

                    var at = lastHits.TryGetValue(steam, out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : DateTimeOffset.UtcNow;

                    return new BountyParticipation(steam, dealt, at);
                })
                .OfType<BountyParticipation>()
                .ToList();

            var participants = attackers
                .Where(p => p.Damage >= MinDamageForParticipation)
                .OrderByDescending(p => p.Damage)
                .ThenBy(p => p.LastHitAt)
                .ToList();

            // The participation bar is a guess at the bridge's damage scale, and a bounty that pays
            // nobody looks identical whether nobody fought the target or everybody fell short of it.
            // Logging both counts and the totals is what tells the two apart.
            if (attackers.Count != participants.Count)
                logger.LogDebug("Bounty {InstanceId}: {Qualified} of {Total} attackers cleared {Threshold} " +
                                "damage. Totals: {Totals}",
                    questInstanceId, participants.Count, attackers.Count, MinDamageForParticipation,
                    string.Join(", ", attackers.Select(a => $"{a.SteamId}={a.Damage:F0}")));

            return participants;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read bounty participants for {InstanceId}", questInstanceId);
            return [];
        }
    }

    /// <summary>The most recent player to hurt the target, whatever the amount.</summary>
    public async Task<BountyParticipation?> LastAttackerAsync(string questInstanceId)
    {
        try
        {
            var lastHits = await Db.HashGetAllAsync(LastHitKey(questInstanceId));
            if (lastHits.Length == 0)
                return null;

            var latest = lastHits.MaxBy(e => (long)e.Value);
            var steam = latest.Name.ToString();

            var dealt = await Db.HashGetAsync(DamageKey(questInstanceId), steam);

            return new BountyParticipation(
                steam,
                TryParseDamage(dealt, out var value) ? value : 0,
                DateTimeOffset.FromUnixTimeMilliseconds((long)latest.Value));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the last attacker for {InstanceId}", questInstanceId);
            return null;
        }
    }

    /// <summary>Reads a <c>HINCRBYFLOAT</c> total.</summary>
    private static bool TryParseDamage(RedisValue value, out double damage)
    {
        damage = 0;
        return value.HasValue
               && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out damage);
    }

    /// <summary>Drops the ledger for a closed bounty. Called from the shared teardown, so it runs on every exit path.</summary>
    public async Task ClearAsync(string questInstanceId)
    {
        try
        {
            await Db.KeyDeleteAsync(DamageKey(questInstanceId));
            await Db.KeyDeleteAsync(LastHitKey(questInstanceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the bounty ledger for {InstanceId}", questInstanceId);
        }
    }
}
