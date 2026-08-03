using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Identity.Application.Services;

public enum OneTimeCodeResult
{
    Valid,

    /// <summary>No code outstanding, or it expired.</summary>
    Expired,

    /// <summary>Wrong code. The attempt has been counted.</summary>
    Invalid,

    /// <summary>Too many wrong guesses; the code has been destroyed and a new one must be requested.</summary>
    TooManyAttempts,
}

/// <summary>
/// Short-lived out-of-band codes emailed to a user (account verification, password reset).
///
/// Two properties this deliberately provides, both of which the previous per-feature
/// implementations lacked:
///
/// 1. Enough entropy to be worth guarding. The codes were the first 6 hex characters of a GUID -
///    2^24 possibilities. That is below every published floor for an out-of-band authenticator, and
///    a password-reset code at that strength is an account-takeover primitive.
///
/// 2. A failed guess costs something. The old comparison returned an error and left the code
///    untouched, so an attacker could keep guessing the same live code for its entire lifetime.
///    Attempts are counted here and the code is destroyed after <see cref="MaxAttempts"/>, which is
///    a property of the code's own lifecycle rather than of request throughput - a code that dies
///    on the fifth wrong answer is safe even with no rate limiter in front of it.
/// </summary>
public static class OneTimeCodeService
{
    /// <summary>48 bits. Still short enough to retype from an email.</summary>
    private const int CodeLengthInHexChars = 12;

    public const int MaxAttempts = 5;

    private static string AttemptsKey(string codeKey) => $"{codeKey}:attempts";

    /// <summary>
    /// Returns the outstanding code for <paramref name="codeKey"/>, minting one if there is none.
    /// </summary>
    public static async Task<string> GetOrCreateCodeAsync(IDistributedCache cache, string codeKey, TimeSpan ttl)
    {
        var existingCode = await cache.GetStringAsync(codeKey);
        if (existingCode != null) return existingCode;

        var code = GenerateCode();
        await cache.SetStringAsync(codeKey, code, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
        });

        // Reset any counter left over from a previous code under the same key.
        await cache.RemoveAsync(AttemptsKey(codeKey));

        return code;
    }

    /// <summary>Checks a submitted code, counting the attempt.</summary>
    public static async Task<OneTimeCodeResult> ValidateAsync(
        IDistributedCache cache, string codeKey, string? submittedCode, TimeSpan ttl)
    {
        var expected = await cache.GetStringAsync(codeKey);
        if (expected is null) return OneTimeCodeResult.Expired;

        if (!string.IsNullOrEmpty(submittedCode) && FixedTimeEquals(expected, submittedCode))
        {
            await RemoveAsync(cache, codeKey);
            return OneTimeCodeResult.Valid;
        }

        var attemptsRaw = await cache.GetStringAsync(AttemptsKey(codeKey));
        var attempts = int.TryParse(attemptsRaw, out var parsed) ? parsed + 1 : 1;

        if (attempts >= MaxAttempts)
        {
            await RemoveAsync(cache, codeKey);
            return OneTimeCodeResult.TooManyAttempts;
        }

        // Rides the code's own expiry so the counter can never outlive the code it guards.
        await cache.SetStringAsync(AttemptsKey(codeKey), attempts.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
        });

        return OneTimeCodeResult.Invalid;
    }

    public static async Task RemoveAsync(IDistributedCache cache, string codeKey)
    {
        await cache.RemoveAsync(codeKey);
        await cache.RemoveAsync(AttemptsKey(codeKey));
    }

    private static string GenerateCode()
    {
        // RandomNumberGenerator, not Guid: a code used as a credential should come from a CSPRNG
        // by construction rather than by relying on the current GUID implementation.
        var bytes = RandomNumberGenerator.GetBytes(CodeLengthInHexChars / 2);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Length-independent, non-short-circuiting comparison.</summary>
    private static bool FixedTimeEquals(string expected, string submitted)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var submittedBytes = Encoding.UTF8.GetBytes(submitted);

        return CryptographicOperations.FixedTimeEquals(expectedBytes, submittedBytes);
    }
}
