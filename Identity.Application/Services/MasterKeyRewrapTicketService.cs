using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

/// <summary>A single-use permit to re-wrap the account master key under a new password.</summary>
public sealed class MasterKeyRewrapTicketService(IDistributedCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static string Key(string userId) => $"master_key_rewrap_ticket:{userId}";

    /// <summary>Mints a ticket for the account, replacing any outstanding one.</summary>
    public async Task<string> IssueAsync(string userId)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await cache.SetStringAsync(Key(userId), ticket, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
        });

        return ticket;
    }

    /// <summary>Redeems a ticket.</summary>
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
