using System.Security.Cryptography;
using System.Text;
using Billing.Domain.Aggregates;
using Microsoft.Extensions.Options;

namespace Billing.Application.Promotions;

/// <summary>Turns an identity into the only form of it this service is allowed to keep.</summary>
public sealed class PromotionHasher(IOptions<PromotionOptions> options)
{
    private readonly PromotionOptions _options = options?.Value ?? new PromotionOptions();

    /// <summary>The hash to store and to match on, or null when there is nothing to hash.</summary>
    public string? Of(PromotionIdentityKind kind, string? value)
    {
        var normalised = Normalise(kind, value);
        if (normalised is null) return null;

        if (!_options.IsConfigured)
        {
            // Unreachable when the startup check ran, and loud rather than silent because the quiet
            // alternative is an instance computing marks under an empty key and looking healthy.
            throw new InvalidOperationException(
                $"An identity hash was attempted with no {PromotionOptions.SaltVariable} configured. "
                + "PromotionOptions.EnsureConfigured is supposed to have refused to start.");
        }

        var key = Encoding.UTF8.GetBytes(_options.HashSalt.Trim());
        var payload = Encoding.UTF8.GetBytes($"{kind}:{normalised}");

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, payload));
    }

    /// <summary>Every hash for one account's device set, deduplicated.</summary>
    public IReadOnlyList<string> OfDevices(IEnumerable<string>? deviceIds)
    {
        if (deviceIds is null) return [];

        var hashes = new List<string>();

        foreach (var id in deviceIds)
        {
            if (Of(PromotionIdentityKind.Device, id) is not { } hash) continue;
            if (!hashes.Contains(hash, StringComparer.Ordinal)) hashes.Add(hash);
        }

        return hashes;
    }

    private static string? Normalise(PromotionIdentityKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        if (kind != PromotionIdentityKind.Phone) return trimmed;

        var digits = new StringBuilder(trimmed.Length);

        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character)) digits.Append(character);
        }

        // Nothing but punctuation, which is not a number and must not become a mark that every other
        // unparseable entry matches.
        return digits.Length == 0 ? null : $"+{digits}";
    }
}
