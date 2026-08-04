using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>
/// How many "someone tried to sign up with your address" mails one address may receive in a window.
/// </summary>
public static class RegistrationNoticeThrottle
{
    /// <summary>Three is enough for the honest case - a user who forgot they had an account and
    /// tried a few times still gets told - and low enough that the endpoint is useless as a mail
    /// cannon.</summary>
    public const int MaxPerWindow = 3;

    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Distinct prefix from <c>verification_code:</c> and <c>password_reset_code:</c>: this
    /// counter must never be confusable with a live code for the same address.</summary>
    private static string Key(string email) => $"registration_notice:{email}";

    /// <summary>Takes one notice from this address's budget.</summary>
    public static async Task<bool> TryAcquireAsync(IDistributedCache cache, string email)
    {
        var key = Key(email);

        var raw = await cache.GetStringAsync(key);
        var sent = int.TryParse(raw, out var parsed) ? parsed : 0;

        if (sent >= MaxPerWindow) return false;

        await cache.SetStringAsync(key, (sent + 1).ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Window,
        });

        return true;
    }
}
