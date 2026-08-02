using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>
/// A single-use permit to re-wrap the account master key under a new password.
///
/// <para><b>Why this exists.</b> <c>POST backup/recovery-key/rewrap-password</c> overwrites the
/// wrapping that makes every backup blob on the account readable. It used to require nothing but a
/// session token, and it cleared <c>MasterKeyPasswordWrappingInvalidatedAt</c> on the way out - so a
/// stolen session could destroy every scrap of encrypted history in one request <i>and erase the
/// signal that anything had happened</i>. The stated justification, that producing a valid wrapping
/// is itself the proof, was never true: nothing checked the wrapping.</para>
///
/// <para>The route cannot simply demand the account password, because the journey it serves is the
/// one right after a reset, when the client is holding the master key unwrapped from the
/// <i>recovery code</i> and is about to seal it under a password the user has only just chosen. So
/// the reset itself - which required the emailed code, a credential the server can verify - mints
/// this ticket and hands it to whoever completed it. Possession of a ticket means "the person who
/// proved control of the account's email a few minutes ago"; that is a real credential, and it is
/// the one the recovery journey actually has.</para>
///
/// <para>Single-use and short-lived. Held in the distributed cache alongside the reset code it is
/// derived from - the code is the more sensitive of the two and already lives there, so a ticket
/// that outlives a cache eviction would be the only thing in the flow that did.</para>
/// </summary>
public sealed class MasterKeyRewrapTicketService(IDistributedCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static string Key(string userId) => $"master_key_rewrap_ticket:{userId}";

    /// <summary>Mints a ticket for the account, replacing any outstanding one. Called only from the
    /// completed-reset path.</summary>
    public async Task<string> IssueAsync(string userId)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await cache.SetStringAsync(Key(userId), ticket, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
        });

        return ticket;
    }

    /// <summary>Redeems a ticket. Removes it before returning true, so a replayed request cannot
    /// re-wrap a second time; a wrong answer leaves the real ticket in place rather than letting a
    /// guesser invalidate it.</summary>
    public async Task<bool> TryConsumeAsync(string userId, string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return false;

        var stored = await cache.GetStringAsync(Key(userId));
        if (stored is null) return false;

        // Fixed-time, and length-checked first: the ticket is a secret the caller is guessing at.
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(stored),
                System.Text.Encoding.UTF8.GetBytes(ticket)))
        {
            return false;
        }

        await cache.RemoveAsync(Key(userId));
        return true;
    }
}
