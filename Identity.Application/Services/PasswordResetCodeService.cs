using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>
/// Password-reset codes. A thin, keyed wrapper over <see cref="OneTimeCodeService"/>, which owns
/// the code's entropy, its constant-time comparison and its failed-attempt lifecycle - see that
/// class for why both matter here in particular: this code is the sole credential standing in
/// front of an account takeover, and the request endpoint is anonymous, so an attacker chooses
/// when a code exists.
/// </summary>
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
