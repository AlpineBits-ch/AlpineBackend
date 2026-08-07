using AppEnvironment;
using StackExchange.Redis;

namespace Guild.Application.Services;

/// <summary>
/// The instance's entire budget for talking to the product database, as one token bucket that every
/// caller spends from.
/// </summary>
public class ProductCatalogRateLimiter(
    IConnectionMultiplexer redis, ILogger<ProductCatalogRateLimiter> logger)
{
    /// <summary>One key for the instance.</summary>
    private const string BucketKey = "pantry:catalog:budget:openfoodfacts";

    /// <summary>Classic token bucket, in Lua because it has to be atomic.</summary>
    private const string TakeScript =
        """
        local capacity = tonumber(ARGV[1])
        local per_second = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local reserve = tonumber(ARGV[4])

        local tokens = tonumber(redis.call('HGET', KEYS[1], 'tokens'))
        local stamped = tonumber(redis.call('HGET', KEYS[1], 'at'))

        if tokens == nil or stamped == nil then
            tokens = capacity
            stamped = now
        end

        local elapsed = now - stamped
        if elapsed < 0 then elapsed = 0 end

        tokens = math.min(capacity, tokens + elapsed * per_second)

        local granted = 0
        if tokens >= 1 + reserve then
            tokens = tokens - 1
            granted = 1
        end

        redis.call('HSET', KEYS[1], 'tokens', tokens, 'at', now)
        redis.call('EXPIRE', KEYS[1], ARGV[5])

        return granted
        """;

    /// <summary>Whether one request may go out now, taking a token if so.</summary>
    public async Task<bool> TryTakeAsync(int reserve = 0, CancellationToken ct = default)
    {
        var config = Env.ProductCatalog;

        var capacity = Math.Max(0, config.BurstCapacity);
        var perMinute = Math.Max(0, config.RequestsPerMinute);

        // A configured zero is an operator saying "no outbound lookups", and it has to be honoured
        // here rather than producing a bucket that refills infinitely slowly and grants the first
        // token anyway.
        if (capacity == 0 || perMinute == 0) return false;

        try
        {
            var database = redis.GetDatabase();

            // Seconds with a fraction, because a bucket refilling at 10 per minute gains a sixth of
            // a token per second and an integer clock would round every refill away to nothing.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

            // Long enough that a bucket cannot expire back to full between two sweeps of a quiet
            // instance, which would be a free reset of the limit every five minutes.
            var ttl = (int)Math.Ceiling(capacity / (perMinute / 60d)) + 60;

            var result = await database.ScriptEvaluateAsync(
                TakeScript,
                [BucketKey],
                [capacity, perMinute / 60d, now, Math.Max(0, reserve), ttl]);

            return !result.IsNull && (long)result == 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Debug rather than warning: on an instance whose Redis is down this would be one line
            // per scan, and the scan path already logs the outage that actually matters.
            logger.LogDebug(e, "Product catalog rate limiter could not be consulted; refusing the lookup");
            return false;
        }
    }
}
