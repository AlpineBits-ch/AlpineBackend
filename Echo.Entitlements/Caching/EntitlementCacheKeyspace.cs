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
    private readonly Func<string>? _catalogueRevision;

    /// <param name="catalogueRevision">
    /// How to ask the registered catalogue source what it is currently answering with.
    /// </param>
    public EntitlementCacheKeyspace(
        string prefix, string fingerprint, Func<string>? catalogueRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        Fingerprint = fingerprint;
        _catalogueRevision = catalogueRevision;
        _prefix = $"{prefix}:{FormatVersion}:{fingerprint}";
    }

    public string Fingerprint { get; }

    /// <summary>What the catalogue source is answering with right now, or <c>fixed</c> when this
    /// keyspace was built without one.</summary>
    public string CatalogueRevision => _catalogueRevision?.Invoke() ?? "fixed";

    /// <summary>The resolved set for a subject.</summary>
    public string SetKey(EntitlementSubject subject) =>
        $"{_prefix}:{CatalogueRevision}:set:{subject.Kind}:{subject.Id}";

    /// <summary>
    /// The entitlement version for a subject, which is a different question with a different owner
    /// (Billing's counter) and therefore a different key.
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
