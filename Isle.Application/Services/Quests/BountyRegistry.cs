using System.Text.Json;
using StackExchange.Redis;

namespace Isle.Api.Services.Quests;

/// <summary>A currently-marked player. Mirrors the open bounty <c>QuestInstance</c> in a form the hot paths can read without a DB hit.</summary>
public sealed record BountyMark(
    string QuestInstanceId,
    string PlayerId,
    string SteamId,
    string? Species,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Who is currently wearing the bounty skin, keyed by Steam id.
///
/// <para>This exists as a Redis-backed lookup rather than a DB query because <c>SkinStore</c> reads it
/// on every join: the SDK's skin-reapply service asks <c>ISkinStore</c> what a joining player should
/// look like, so a marked player who reconnects mid-bounty must come back still marked. That is also
/// why the marks survive an API restart — a bounty outliving the process would otherwise leave a
/// player permanently pale with nothing tracking them.</para>
/// </summary>
public sealed class BountyRegistry(IConnectionMultiplexer redis, ILogger<BountyRegistry> logger)
{
    private const string Key = "isle:bounty:marks";

    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(12);

    private IDatabase Db => redis.GetDatabase();

    public async Task MarkAsync(BountyMark mark)
    {
        try
        {
            await Db.HashSetAsync(Key, mark.SteamId, JsonSerializer.Serialize(mark));
            await Db.KeyExpireAsync(Key, KeyTtl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record bounty mark for {Steam}", mark.SteamId);
        }
    }

    public async Task UnmarkAsync(string steamId)
    {
        try
        {
            await Db.HashDeleteAsync(Key, steamId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear bounty mark for {Steam}", steamId);
        }
    }

    /// <summary>The live mark for a Steam id, or null. An expired mark is treated as absent and swept.</summary>
    public async Task<BountyMark?> GetBySteamAsync(string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return null;

        try
        {
            var value = await Db.HashGetAsync(Key, steamId);
            if (!value.HasValue)
                return null;

            var mark = TryDeserialize(value.ToString());
            if (mark is null)
                return null;

            if (mark.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                // Self-heal: the sweep should have removed this, but a marked player must never be
                // left wearing the skin because a tick was missed.
                await UnmarkAsync(steamId);
                return null;
            }

            return mark;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read bounty mark for {Steam}", steamId);
            return null;
        }
    }

    public async Task<IReadOnlyList<BountyMark>> GetAllAsync()
    {
        try
        {
            var entries = await Db.HashGetAllAsync(Key);
            var now = DateTimeOffset.UtcNow;

            return entries
                .Select(e => e.Value.HasValue ? TryDeserialize(e.Value.ToString()) : null)
                .OfType<BountyMark>()
                .Where(m => m.ExpiresAt > now)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read bounty marks");
            return [];
        }
    }

    public async Task<bool> IsMarkedAsync(string steamId) => await GetBySteamAsync(steamId) is not null;

    private BountyMark? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BountyMark>(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding unreadable bounty mark");
            return null;
        }
    }
}
