using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>
/// Email-verification codes. Keyed wrapper over <see cref="OneTimeCodeService"/> - see that class
/// for the entropy and failed-attempt guarantees.
/// </summary>
public static class VerificationCodeService
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static string Key(string email) => $"verification_code:{email}";

    public static Task<string> GetOrCreateCodeAsync(IDistributedCache cache, string email) =>
        OneTimeCodeService.GetOrCreateCodeAsync(cache, Key(email), Ttl);

    public static Task<OneTimeCodeResult> ValidateAsync(IDistributedCache cache, string email, string? submittedCode) =>
        OneTimeCodeService.ValidateAsync(cache, Key(email), submittedCode, Ttl);

    public static Task RemoveAsync(IDistributedCache cache, string email) =>
        OneTimeCodeService.RemoveAsync(cache, Key(email));
}
