using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>
/// How many "someone tried to sign up with your address" mails one address may receive in a window.
///
/// <para><b>This is the price of closing the registration enumeration oracle, and it has to be paid
/// deliberately.</b> <c>POST api/v1/authentication/register</c> is anonymous, and it now sends mail
/// to an address the caller does not control on the branch where that address already has an
/// account. Without a cap, anyone can point the endpoint at a victim's address in a loop and turn
/// signup into a mail bomb aimed at a third party - a worse problem than the leak being fixed.</para>
///
/// <para>Same shape as <see cref="OneTimeCodeService"/>'s attempt counter, and deliberately so: a
/// counter in the same Redis <see cref="IDistributedCache"/> the verification and reset codes already
/// live in, keyed by address, expiring on its own. No new dependency, no new failure mode, and it
/// works across pods because the store is shared.</para>
///
/// <para><b>The count is not atomic.</b> <see cref="IDistributedCache"/> exposes no increment, so two
/// pods racing on the same address can both read the same value and both send - the cap is a bound
/// on mail volume, not a security boundary, and one extra mail per racing pod is an acceptable miss.
/// An address under sustained attack still receives <see cref="MaxPerWindow"/> mails a day rather
/// than one per request, which is the property that matters. The alternative - a Lua INCR against
/// StackExchange.Redis directly - would tie this service to a concrete cache implementation that
/// every test host swaps out for an in-memory one.</para>
///
/// <para>The window is anchored on the last mail actually sent, not on the first attempt: the entry
/// is only rewritten while the address is under the cap, so a flood produces
/// <see cref="MaxPerWindow"/> mails and then silence until <see cref="Window"/> after the last
/// one.</para>
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

    /// <summary>
    /// Takes one notice from this address's budget. False means the mail must not be sent.
    ///
    /// <para>Callers must treat the result as invisible to the HTTP response - the whole point of
    /// the exists-branch is that it looks identical whether or not a mail went out, and a caller
    /// that surfaced "throttled" would hand back the same oracle in a new shape.</para>
    /// </summary>
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
