using Echo.Entitlements.Model;
using Microsoft.Extensions.Logging;

namespace Echo.Entitlements.Caching;

/// <summary>What a <c>billing.EntitlementsChanged</c> consumer calls.</summary>
public sealed class EntitlementCacheInvalidator(
    IEntitlementCacheStore store,
    EntitlementCacheKeyspace keyspace,
    EntitlementSetCodec codec,
    EntitlementCacheOptions options,
    ILogger<EntitlementCacheInvalidator> logger,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task InvalidateAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        var setKey = keyspace.SetKey(subject);
        var versionKey = keyspace.VersionKey(subject);

        try
        {
            // The version is a plain counter with no fallback value worth keeping: a stale one is
            // worse than none, because a client compares it against what it was pushed and a number
            // from before the change tells it to stop asking. So that one is dropped outright.
            await store.RemoveAsync(versionKey, cancellationToken).ConfigureAwait(false);

            var cached = codec.Decode(await store.ReadAsync(setKey, cancellationToken).ConfigureAwait(false));

            if (cached is not { } entry)
            {
                // Nothing cached, or a payload this build cannot read.
                await store.RemoveAsync(setKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = _clock.GetUtcNow();

            if (!entry.IsFreshAt(now)) return;

            await store.WriteAsync(
                setKey, codec.Encode(entry.Set, now), options.Retain, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not invalidate the cached entitlements for {Subject}. The change takes effect when "
                + "the entry's {Ttl} TTL expires instead.", subject, options.Ttl);
        }
    }

    public Task InvalidateAsync(
        SubjectKind kind, string subjectId, CancellationToken cancellationToken = default) =>
        InvalidateAsync(new EntitlementSubject(kind, subjectId), cancellationToken);
}
