using System.Security.Cryptography;
using System.Text;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Entitlements.Caching;

/// <summary>Where one subject's resolved set lives in Redis.</summary>
public sealed class EntitlementCacheKeyspace
{
    /// <summary>The payload format, not the entitlement vocabulary.</summary>
    public const string FormatVersion = "v1";

    private readonly string _prefix;

    public EntitlementCacheKeyspace(string prefix, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        Fingerprint = fingerprint;
        _prefix = $"{prefix}:{FormatVersion}:{fingerprint}";
    }

    public string Fingerprint { get; }

    /// <summary>The resolved set for a subject.</summary>
    public string SetKey(EntitlementSubject subject) => $"{_prefix}:set:{subject.Kind}:{subject.Id}";

    /// <summary>The entitlement version for a subject, which is a different question with a different
    /// owner (Billing's counter) and therefore a different key. Folding it into the set would tie a
    /// number that changes on every write to a payload whose whole point is that it does not.
    /// </summary>
    public string VersionKey(EntitlementSubject subject) => $"{_prefix}:ver:{subject.Kind}:{subject.Id}";

    /// <summary>A stable short name for "the configuration that produced this answer".</summary>
    public static string FingerprintOf(
        IEnumerable<IEntitlementSource> sources, EntitlementPlanOptions? plans)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var text = new StringBuilder();

        // Precedence rather than type name: two services can reach grants by different transports -
        // a query inside Billing, a bus call outside it - and those produce the same answer.
        foreach (var precedence in sources.Select(source => source.Precedence).OrderBy(band => band))
        {
            text.Append(precedence).Append(';');
        }

        text.Append('|');

        if (plans is not null)
        {
            text.Append(plans.DefaultGuildPlan).Append(';').Append(plans.DefaultUserPlan).Append('|');

            foreach (var (name, values) in plans.Plans.OrderBy(plan => plan.Key, StringComparer.Ordinal))
            {
                text.Append(name).Append(':');
                foreach (var (key, value) in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    text.Append(key).Append('=').Append(value).Append(',');
                }

                text.Append(';');
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
