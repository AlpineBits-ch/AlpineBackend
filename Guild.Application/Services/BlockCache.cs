using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>Every block one user is party to, in either direction.</summary>
public sealed class UserBlockState
{
    /// <summary>Users this user pressed block on.</summary>
    public List<string> Blocked { get; set; } = [];

    /// <summary>Users who pressed block on this user.</summary>
    public List<string> BlockedBy { get; set; } = [];
}

/// <summary>
/// The answer to "may these two reach each other", for one batch, with the resolution failures
/// carried inside it rather than thrown.
/// </summary>
public sealed class BlockView
{
    private readonly IReadOnlyDictionary<string, UserBlockState?> _states;

    internal BlockView(IReadOnlyDictionary<string, UserBlockState?> states) => _states = states;

    /// <summary>A view over nobody.</summary>
    public static BlockView Unresolved(IEnumerable<string> userIds) =>
        new(userIds.ToDictionary(id => id, _ => (UserBlockState?)null, StringComparer.Ordinal));

    /// <summary>A view that knows there are no blocks at all.</summary>
    public static BlockView Empty { get; } =
        new(new Dictionary<string, UserBlockState?>(StringComparer.Ordinal));

    /// <summary>Whether a block exists between the pair, in either direction.</summary>
    public bool AreBlocked(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return false;

        var resolvedEither = false;

        if (_states.TryGetValue(a, out var stateA) && stateA is not null)
        {
            resolvedEither = true;
            if (Contains(stateA, b)) return true;
        }

        if (_states.TryGetValue(b, out var stateB) && stateB is not null)
        {
            resolvedEither = true;
            if (Contains(stateB, a)) return true;
        }

        return !resolvedEither;
    }

    /// <summary>The members of <paramref name="others"/> that <paramref name="subject"/> may still
    /// reach, and that may still reach <paramref name="subject"/>.</summary>
    public List<string> Reachable(string subject, IEnumerable<string> others) =>
        others.Where(other => !AreBlocked(subject, other)).ToList();

    private static bool Contains(UserBlockState state, string userId) =>
        state.Blocked.Contains(userId, StringComparer.Ordinal) ||
        state.BlockedBy.Contains(userId, StringComparer.Ordinal);
}

/// <summary>Guild's read-through view of Social's block list (privacy spec T0-3).</summary>
public class BlockCache(
    IDistributedCache cache,
    IMessageBus bus,
    ILogger<BlockCache> logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public static string KeyFor(string userId) => $"blocks:user_id:{userId}";

    /// <summary>
    /// Resolves the block state of the given users - normally just the one subject of a fan-out.
    /// </summary>
    public async Task<BlockView> GetAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var states = new Dictionary<string, UserBlockState?>(StringComparer.Ordinal);
        if (ids.Count == 0) return new BlockView(states);

        var missing = new List<string>();

        foreach (var id in ids)
        {
            var hit = await TryReadAsync(id, ct);
            if (hit is not null) states[id] = hit;
            else missing.Add(id);
        }

        if (missing.Count == 0) return new BlockView(states);

        try
        {
            var response = await bus.InvokeAsync<GetBlockRelationshipsResponse>(
                new GetBlockRelationshipsRequest { UserIds = missing }, ct);

            // Social returns every block touching any listed id, on either side, so one pass over
            // the response builds every requested state.
            foreach (var id in missing) states[id] = new UserBlockState();

            foreach (var block in response.Blocks)
            {
                if (states.TryGetValue(block.BlockerId, out var blocker) && blocker is not null)
                    blocker.Blocked.Add(block.BlockedId);

                if (states.TryGetValue(block.BlockedId, out var blocked) && blocked is not null)
                    blocked.BlockedBy.Add(block.BlockerId);
            }

            foreach (var id in missing) await TryWriteAsync(id, states[id]!, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Could not resolve block relationships for {Count} user(s) from Social; treating their pairs as blocked",
                missing.Count);

            // Explicitly null rather than an empty state: an empty state means "resolved, no
            // blocks", and that is the one thing this must not be mistaken for.
            foreach (var id in missing) states[id] = null;
        }

        return new BlockView(states);
    }

    /// <summary>Drops one user's entry.</summary>
    public async Task InvalidateAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            await cache.RemoveAsync(KeyFor(userId), ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not evict cached block state for user {UserId}", userId);
        }
    }

    private async Task<UserBlockState?> TryReadAsync(string userId, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(KeyFor(userId), ct);
            if (bytes is null || bytes.Length == 0) return null;
            return JsonSerializer.Deserialize<UserBlockState>(bytes);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read cached block state for user {UserId}", userId);
            return null;
        }
    }

    private async Task TryWriteAsync(string userId, UserBlockState state, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(
                KeyFor(userId),
                JsonSerializer.SerializeToUtf8Bytes(state),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not cache block state for user {UserId}", userId);
        }
    }
}
