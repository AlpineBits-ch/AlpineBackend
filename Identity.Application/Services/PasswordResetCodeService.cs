using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>Mirrors VerificationCodeService's "reuse an existing still-valid code" pattern -
/// same reasoning applies here: a resend click or a redelivered request must not invalidate a
/// code the user already has open in their inbox (see project memory on the verification-code
/// overwrite race this codebase hit previously).</summary>
public static class PasswordResetCodeService
{
    public static async Task<string> GetOrCreateCodeAsync(IDistributedCache cache, string email)
    {
        var key = $"password_reset_code:{email}";

        var existingCode = await cache.GetStringAsync(key);
        if (existingCode != null) return existingCode;

        var code = Guid.NewGuid().ToString("N").Substring(0, 6);
        await cache.SetStringAsync(key, code, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
        });
        return code;
    }

    public static Task RemoveAsync(IDistributedCache cache, string email) =>
        cache.RemoveAsync($"password_reset_code:{email}");
}
