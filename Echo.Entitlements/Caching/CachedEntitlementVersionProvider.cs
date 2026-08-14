using System.Globalization;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Microsoft.Extensions.Logging;

namespace Echo.Entitlements.Caching;

/// <summary>The entitlement version, cached on the same terms as the set it rides with.</summary>
public sealed class CachedEntitlementVersionProvider(
    IEntitlementVersionProvider inner,
    IEntitlementCacheStore store,
    EntitlementCacheKeyspace keyspace,
    EntitlementCacheOptions options,
    ILogger<CachedEntitlementVersionProvider> logger,
    TimeProvider? clock = null) : IEntitlementVersionProvider
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<long> VersionAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        var key = keyspace.VersionKey(subject);
        var now = _clock.GetUtcNow();
        var cached = Parse(await store.ReadAsync(key, cancellationToken).ConfigureAwait(false));

        if (cached is { } entry && now < entry.FreshUntil) return entry.Version;

        try
        {
            var version = await inner.VersionAsync(subject, cancellationToken).ConfigureAwait(false);
            await WriteAsync(key, version, now + options.Ttl, options.Retain, cancellationToken)
                .ConfigureAwait(false);
            return version;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var fallback = cached?.Version ?? 0;

            logger.LogWarning(exception,
                "Could not read the entitlement version for {Subject}. Answering {Version} and retrying "
                + "in {Grace}.", subject, fallback, options.OutageGrace);

            await WriteAsync(key, fallback, now + options.OutageGrace, options.Retain, cancellationToken)
                .ConfigureAwait(false);

            return fallback;
        }
    }

    private Task WriteAsync(
        string key, long version, DateTimeOffset freshUntil, TimeSpan keepFor, CancellationToken cancellationToken) =>
        store.WriteAsync(
            key,
            string.Create(CultureInfo.InvariantCulture, $"{freshUntil.ToUnixTimeMilliseconds()}:{version}"),
            keepFor,
            cancellationToken);

    /// <summary>Two numbers and a colon rather than JSON.</summary>
    private static (DateTimeOffset FreshUntil, long Version)? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var split = payload.IndexOf(':');
        if (split <= 0 || split == payload.Length - 1) return null;

        return long.TryParse(payload.AsSpan(0, split), CultureInfo.InvariantCulture, out var freshUntil)
               && long.TryParse(payload.AsSpan(split + 1), CultureInfo.InvariantCulture, out var version)
            ? (DateTimeOffset.FromUnixTimeMilliseconds(freshUntil), version)
            : null;
    }
}
