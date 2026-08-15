using StackExchange.Redis;

namespace Guild.Application.Services;

/// <summary>A per-caller budget for the two unauthenticated invite-preview routes.</summary>
public class InvitePreviewRateLimiter(IConnectionMultiplexer redis, ILogger<InvitePreviewRateLimiter> logger)
{
    /// <summary>Sustained rate, per caller.</summary>
    public const int PerMinute = 30;

    /// <summary>Burst.</summary>
    public const int BurstCapacity = 60;

    private const string TakeScript =
        """
        local capacity = tonumber(ARGV[1])
        local per_second = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])

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
        if tokens >= 1 then
            tokens = tokens - 1
            granted = 1
        end

        redis.call('HSET', KEYS[1], 'tokens', tokens, 'at', now)
        redis.call('EXPIRE', KEYS[1], ARGV[4])

        return granted
        """;

    /// <summary>Whether this caller may preview one invite now, spending a token if so.</summary>
    public virtual async Task<bool> TryTakeAsync(string partition, CancellationToken ct = default)
    {
        try
        {
            var database = redis.GetDatabase();

            // Fractional seconds: at 30 a minute a bucket gains half a token per second, and an
            // integer clock would round every refill away to nothing.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            var ttl = (int)Math.Ceiling(BurstCapacity / (PerMinute / 60d)) + 60;

            var result = await database.ScriptEvaluateAsync(
                TakeScript,
                [(RedisKey)$"guild:invite-preview:{partition}"],
                [BurstCapacity, PerMinute / 60d, now, ttl]);

            return result.IsNull || (long)result == 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Invite preview rate limiter could not be consulted; allowing the preview");
            return true;
        }
    }

    /// <summary>
    /// The bucket a request belongs to: the authenticated subject when there is one, the client
    /// address otherwise.
    /// </summary>
    public static string PartitionFor(HttpContext context)
    {
        var subject = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subject)) return $"u:{subject}";

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
