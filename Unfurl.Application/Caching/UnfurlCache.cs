using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppEnvironment;
using Bots.Contracts.Gateway.Payloads;
using Microsoft.Extensions.Caching.Distributed;

namespace Unfurl.Application.Caching;

/// <summary>What we last learned about a URL - including that it was a dead end.</summary>
public sealed class CachedUnfurl
{
    public EmbedPayload? Embed { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>Redis-backed memory of what each URL resolved to.</summary>
public class UnfurlCache(IDistributedCache cache, ILogger<UnfurlCache> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Bumping this invalidates every cached entry at once.</summary>
    private const string SchemaVersion = "v1";

    public static string KeyFor(string url) =>
        $"unfurl:{SchemaVersion}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))}";

    public async Task<CachedUnfurl?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(KeyFor(url), ct);
            if (bytes is null || bytes.Length == 0) return null;

            return JsonSerializer.Deserialize<CachedUnfurl>(bytes, Json);
        }
        catch (Exception e)
        {
            // A cache that is down means slower previews, not broken ones.
            logger.LogWarning(e, "Could not read the unfurl cache");
            return null;
        }
    }

    public Task SetAsync(string url, EmbedPayload embed, TimeSpan? originTtl, CancellationToken ct) =>
        WriteAsync(url, new CachedUnfurl { Embed = embed }, ResolveTtl(originTtl), ct);

    public Task SetFailureAsync(string url, string reason, CancellationToken ct) =>
        WriteAsync(url, new CachedUnfurl { FailureReason = reason }, Env.Unfurl.FailureCacheTtl, ct);

    private async Task WriteAsync(string url, CachedUnfurl entry, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(
                KeyFor(url),
                JsonSerializer.SerializeToUtf8Bytes(entry, Json),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not write the unfurl cache");
        }
    }

    /// <summary>
    /// Honours the origin's own <c>Cache-Control: max-age</c>, clamped at both ends.
    /// </summary>
    private static TimeSpan ResolveTtl(TimeSpan? originTtl)
    {
        if (originTtl is null) return Env.Unfurl.DefaultCacheTtl;

        if (originTtl <= TimeSpan.Zero) return Env.Unfurl.MinCacheTtl;

        return TimeSpan.FromTicks(Math.Clamp(
            originTtl.Value.Ticks,
            Env.Unfurl.MinCacheTtl.Ticks,
            Env.Unfurl.MaxCacheTtl.Ticks));
    }
}
