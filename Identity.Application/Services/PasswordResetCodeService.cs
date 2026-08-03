using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>Password-reset codes.</summary>
public static class PasswordResetCodeService
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private static string Key(string email) => $"password_reset_code:{email}";

    public static Task<string> GetOrCreateCodeAsync(IDistributedCache cache, string email) =>
        OneTimeCodeService.GetOrCreateCodeAsync(cache, Key(email), Ttl);

    public static Task<OneTimeCodeResult> ValidateAsync(IDistributedCache cache, string email, string? submittedCode) =>
        OneTimeCodeService.ValidateAsync(cache, Key(email), submittedCode, Ttl);

    public static Task RemoveAsync(IDistributedCache cache, string email) =>
        OneTimeCodeService.RemoveAsync(cache, Key(email));
}
