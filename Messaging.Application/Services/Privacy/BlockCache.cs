using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// Every block one account is party to, in both directions.
///
/// <para><see cref="Blocking"/> is who they blocked; <see cref="BlockedBy"/> is who blocked them.
/// The two are kept apart because the refusal a caller may show differs: the person who pressed
/// block is entitled to be told "you blocked them", and the person who was blocked must never be
/// able to tell a block from ordinary unfriendliness (T0-3).</para>
///
/// <para><see cref="Degraded"/> says the answer is a guess, not a fact - Social could not be
/// reached and nothing usable was cached. Callers on an authorization path must refuse rather than
/// proceed.</para>
/// </summary>
public sealed record BlockRelations(
    IReadOnlySet<string> Blocking,
    IReadOnlySet<string> BlockedBy,
    bool Degraded)
{
    public static BlockRelations Empty { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), false);

    public static BlockRelations Unknown { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), true);

    public bool AnyBlockWith(string otherUserId) =>
        Blocking.Contains(otherUserId) || BlockedBy.Contains(otherUserId);
}

/// <summary>
/// Messaging's read-through view of Social's block list, keyed <c>blocks:user_id:{id}</c> per §1.4
/// of docs/specs/privacy.md and evicted by <c>UserBlockedEvent</c>/<c>UserUnblockedEvent</c>.
///
/// <para>Immediate eviction is the whole point of the event: a block that takes effect at the next
/// cache expiry is a block that does not stop the thing the user pressed the button to stop.</para>
///
/// <para>Same three-step fallback as <see cref="PrivacySettingsCache"/> - fresh entry, then the
/// last known entry however old, then <see cref="BlockRelations.Unknown"/>, which every
/// authorization caller must treat as a refusal.</para>
/// </summary>
public sealed class BlockCache(
    IDistributedCache cache,
    IMessageBus bus,
    ILogger<BlockCache> logger)
{
    public static string KeyFor(string userId) => $"blocks:user_id:{userId}";

    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HardExpiry = TimeSpan.FromHours(24);

    private sealed record Envelope(List<string> Blocking, List<string> BlockedBy, DateTimeOffset FetchedAt);

    /// <summary>
    /// Every block involving <paramref name="userId"/>. One request answers both directions, which
    /// is why the contract returns blocks where a listed user appears on either side.
    /// </summary>
    public async Task<BlockRelations> GetAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return BlockRelations.Empty;

        var cached = await ReadAsync(userId, ct);
        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < RefreshAfter)
            return Materialize(cached);

        GetBlockRelationshipsResponse? response = null;
        try
        {
            response = await bus.InvokeAsync<GetBlockRelationshipsResponse>(
                new GetBlockRelationshipsRequest { UserIds = [userId] }, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Block lookup failed for {UserId}", userId);
        }

        if (response is null)
        {
            // Stale beats invented. Only with nothing at all does the caller get told the answer is
            // unknown, which on an authorization path means refuse.
            return cached is not null ? Materialize(cached) : BlockRelations.Unknown;
        }

        var blocking = new List<string>();
        var blockedBy = new List<string>();

        foreach (var block in response.Blocks)
        {
            if (string.Equals(block.BlockerId, userId, StringComparison.Ordinal)) blocking.Add(block.BlockedId);
            if (string.Equals(block.BlockedId, userId, StringComparison.Ordinal)) blockedBy.Add(block.BlockerId);
        }

        var envelope = new Envelope(blocking, blockedBy, DateTimeOffset.UtcNow);
        await WriteAsync(userId, envelope, ct);
        return Materialize(envelope);
    }

    /// <summary>Whether a block exists in either direction between the two. Resolved from one
    /// user's record, so it costs a single lookup.</summary>
    public async Task<BlockRelations> BetweenAsync(string userId, CancellationToken ct = default) =>
        await GetAsync(userId, ct);

    /// <summary>
    /// Which of <paramref name="candidates"/> are on either side of a block with
    /// <paramref name="subjectUserId"/>. Used where a fan-out has to be trimmed rather than
    /// refused - a push list, a mention list.
    /// </summary>
    public async Task<IReadOnlySet<string>> BlockedEitherWayAsync(
        string subjectUserId, IEnumerable<string> candidates, CancellationToken ct = default)
    {
        var relations = await GetAsync(subjectUserId, ct);
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            // Degraded means "assume everything is blocked": suppressing a notification that should
            // have gone out is recoverable, delivering one the recipient blocked is not.
            if (relations.Degraded || relations.AnyBlockWith(candidate)) set.Add(candidate);
        }

        return set;
    }

    public async Task InvalidateAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            await cache.RemoveAsync(KeyFor(userId), ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to evict cached blocks for {UserId}", userId);
        }
    }

    private static BlockRelations Materialize(Envelope envelope) => new(
        new HashSet<string>(envelope.Blocking, StringComparer.Ordinal),
        new HashSet<string>(envelope.BlockedBy, StringComparer.Ordinal),
        Degraded: false);

    private async Task<Envelope?> ReadAsync(string userId, CancellationToken ct)
    {
        try
        {
            var raw = await cache.GetStringAsync(KeyFor(userId), ct);
            if (string.IsNullOrEmpty(raw)) return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(raw);
            return envelope is null || envelope.Blocking is null || envelope.BlockedBy is null ? null : envelope;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Cached blocks for {UserId} were unreadable", userId);
            return null;
        }
    }

    private async Task WriteAsync(string userId, Envelope envelope, CancellationToken ct)
    {
        try
        {
            await cache.SetStringAsync(
                KeyFor(userId),
                JsonSerializer.Serialize(envelope),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = HardExpiry },
                ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to cache blocks for {UserId}", userId);
        }
    }
}
