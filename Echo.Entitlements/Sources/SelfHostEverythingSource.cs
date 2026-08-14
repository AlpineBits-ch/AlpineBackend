using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Echo.Entitlements.Sources;

/// <summary>
/// Which of the two things this deployment is: somebody's own server, or the hosted product.
/// </summary>
public enum LicenseMode
{
    /// <summary>The default, everywhere, always. See <see cref="SelfHostEverythingSource"/>.</summary>
    SelfHost,

    /// <summary>The hosted product, where the sources below the license band decide.</summary>
    Hosted,
}

public static class LicenseModes
{
    public const string SelfHostName = "selfhost";
    public const string HostedName = "hosted";

    /// <summary>The mode named by a configuration string.</summary>
    public static LicenseMode Parse(string? mode)
    {
        var trimmed = mode?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return LicenseMode.SelfHost;

        if (string.Equals(trimmed, SelfHostName, StringComparison.OrdinalIgnoreCase)) return LicenseMode.SelfHost;
        if (string.Equals(trimmed, HostedName, StringComparison.OrdinalIgnoreCase)) return LicenseMode.Hosted;

        throw new ArgumentException(
            $"LICENSE_MODE is '{mode}', which is neither '{SelfHostName}' nor '{HostedName}'.",
            nameof(mode));
    }
}

/// <summary>
/// Self-hosting, and the whole of it: every key at its maximum, nothing below consulted.
/// </summary>
public sealed class SelfHostEverythingSource : IEntitlementSource
{
    private readonly EntitlementSet _everything;

    /// <param name="catalogue">Defaults to <see cref="EntitlementKeys.All"/>.</param>
    public SelfHostEverythingSource(IReadOnlyList<EntitlementKey>? catalogue = null)
    {
        var builder = new EntitlementSetBuilder(EntitlementPrecedence.LicenseMode);

        foreach (var key in catalogue ?? EntitlementKeys.All)
        {
            builder.Set(key, Maximum(key), LicenseModes.SelfHostName);
        }

        _everything = builder.Build();
    }

    public EntitlementPrecedence Precedence => EntitlementPrecedence.LicenseMode;

    /// <summary>Always.</summary>
    public bool ShortCircuits => true;

    public Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken) =>
        Task.FromResult(_everything);

    /// <summary>The top of a key's own order: a flag granted, a numeric limit unlimited, a ladder at
    /// its highest rung. Every key in the catalogue is one of the three, and a fourth value kind
    /// would fail here rather than silently resolving to whatever a default gave it.</summary>
    public static EntitlementValue Maximum(EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.ValueKind switch
        {
            EntitlementValueKind.Flag => EntitlementValue.OfFlag(true),
            EntitlementValueKind.Numeric => EntitlementValue.OfNumber(EntitlementValue.Unlimited),
            EntitlementValueKind.Ladder => EntitlementValue.OfRank(key.Ladder!.HighestRank),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.ValueKind,
                $"Key '{key.Name}' has a value kind self-hosting does not know how to maximise."),
        };
    }
}

/// <summary>What this box will do, as opposed to what a guild is allowed.</summary>
public sealed class OperatorCeilings
{
    /// <summary>No ceilings, which is the shipped state and the one every existing deployment is
    /// already in. <see cref="Clamp"/> is then the identity function.</summary>
    public static readonly OperatorCeilings None = new(new Dictionary<EntitlementKey, EntitlementValue>());

    private readonly IReadOnlyDictionary<EntitlementKey, EntitlementValue> _ceilings;

    public OperatorCeilings(IReadOnlyDictionary<EntitlementKey, EntitlementValue> ceilings)
    {
        ArgumentNullException.ThrowIfNull(ceilings);

        foreach (var (key, value) in ceilings)
        {
            if (value.Kind != key.ValueKind)
            {
                throw new ArgumentException(
                    $"Operator ceiling for '{key.Name}' is a {value.Kind} value, but that key is {key.ValueKind}.",
                    nameof(ceilings));
            }
        }

        _ceilings = ceilings;
    }

    public bool IsEmpty => _ceilings.Count == 0;

    public bool TryGet(EntitlementKey key, out EntitlementValue ceiling) =>
        _ceilings.TryGetValue(key, out ceiling);

    /// <summary>
    /// The lower of the entitlement and the operator's ceiling, or the entitlement unchanged when
    /// this box sets no ceiling on that key.
    /// </summary>
    public EntitlementValue Clamp(EntitlementKey key, EntitlementValue entitled)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (entitled.Kind != key.ValueKind)
        {
            throw new ArgumentException(
                $"Key '{key.Name}' is {key.ValueKind}; a {entitled.Kind} value was supplied.", nameof(entitled));
        }

        return _ceilings.TryGetValue(key, out var ceiling)
            ? EntitlementValue.Restrict(entitled, ceiling)
            : entitled;
    }

    /// <summary>What an enforcement site should actually allow: the resolved entitlement, clamped.
    /// The one call a choke point makes, so that reading the set and forgetting the clamp is not a
    /// shape the enforcement code can take.</summary>
    public EntitlementValue Effective(EntitlementSet resolved, EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(key);

        return Clamp(key, resolved.Value(key));
    }

    /// <summary>True when this box's ceiling is what actually bound, which is how an enforcement
    /// site knows to report <see cref="EntitlementDegradationReason.OperatorCeiling"/> rather than a
    /// plan limit somebody could pay to lift.</summary>
    public bool Binds(EntitlementKey key, EntitlementValue entitled) =>
        Clamp(key, entitled).Raw < entitled.Raw;

    /// <summary>
    /// Ceilings from the string form configuration arrives in, keyed by entitlement key name.
    /// </summary>
    public static OperatorCeilings Parse(IReadOnlyDictionary<string, string?> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();

        foreach (var (name, text) in configured)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var key = EntitlementKeys.Require(name);
            parsed[key] = key.Parse(text);
        }

        return parsed.Count == 0 ? None : new OperatorCeilings(parsed);
    }
}

public static class LicenseEntitlementServiceCollectionExtensions
{
    /// <summary>Registers the license mode and the operator's own ceilings.</summary>
    public static IServiceCollection AddLicenseMode(
        this IServiceCollection services, LicenseMode mode, OperatorCeilings? ceilings = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(ceilings ?? OperatorCeilings.None);

        if (mode == LicenseMode.SelfHost)
        {
            services.AddSingleton<IEntitlementSource>(_ => new SelfHostEverythingSource());
        }

        return services;
    }
}
