using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

public static class VerificationCodeService
{
    public static async Task<string> GetOrCreateCodeAsync(IDistributedCache cache, string email)
    {
        var key = $"verification_code:{email}";

        // Reuse an already-issued, still-valid code instead of minting a new one.
        // Both the automatic post-signup email and the on-demand resend endpoint
        // call this, and either can fire more than once for the same user (a
        // resend click, a redelivered domain event picked up by another pod).
        // Unconditionally overwriting the cache entry would silently invalidate
        // whichever code the user already has in their inbox.
        var existingCode = await cache.GetStringAsync(key);
        if (existingCode != null) return existingCode;

        var code = Guid.NewGuid().ToString("N").Substring(0, 6);
        await cache.SetStringAsync(key, code, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });
        return code;
    }
}
