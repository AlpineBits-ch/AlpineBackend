using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

public static class VerificationCodeService
{
    public static async Task<string> GetOrCreateCodeAsync(IDistributedCache cache, string email)
    {
        var key = $"verification_code:{email}";

        // Reuse an already-issued, still-valid code instead of minting a new one.
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
