using Echo.Entitlements.Keys;

namespace Echo.Entitlements.Model;

/// <summary>
/// Where a value came from, in the order sources are consulted (spec section 4.2).
/// </summary>
public enum EntitlementPrecedence
{
    /// <summary>Self-host.</summary>
    LicenseMode = 1,

    /// <summary>Staff-issued, permanent or expiring.</summary>
    AdminGrant = 2,

    /// <summary>Campaign-scoped and always expiring. Credit-funded purchases land here too.</summary>
    PromotionalGrant = 3,

    /// <summary>Stripe, or any other payment source at the same standing - an App Store receipt
    /// belongs here rather than in a category of its own.</summary>
    Subscription = 4,

    /// <summary>Member-funded boosts, converted to a tier.</summary>
    Boost = 5,

    /// <summary>The plan the subject is on, Free included.</summary>
    PlanDefault = 6,

    /// <summary>Nothing supplied this key and it fell to its catalogue default.</summary>
    CatalogueDefault = 7,
}

/// <summary>One resolved key: its value and the source credited with it.</summary>
/// <param name="Source">The precedence band that supplied <paramref name="Value"/>.</param>
/// <param name="Detail">Optional identifier of the specific record within that band - a grant id, a
/// plan name, a Stripe subscription id. The admin console shows it verbatim, so it should name
/// something a human can then go and look at.</param>
public readonly record struct EntitlementProvenance(EntitlementPrecedence Source, string? Detail = null)
{
    public static readonly EntitlementProvenance CatalogueDefault =
        new(EntitlementPrecedence.CatalogueDefault);

    public override string ToString() => Detail is null ? Source.ToString() : $"{Source} ({Detail})";
}

public readonly record struct EntitlementEntry(
    EntitlementKey Key,
    EntitlementValue Value,
    EntitlementProvenance Provenance);

/// <summary>A resolved key-to-value map with per-key provenance.</summary>
public sealed class EntitlementSet
{
    public static readonly EntitlementSet Empty = new(new Dictionary<EntitlementKey, EntitlementEntry>());

    private readonly IReadOnlyDictionary<EntitlementKey, EntitlementEntry> _entries;

    internal EntitlementSet(IReadOnlyDictionary<EntitlementKey, EntitlementEntry> entries) =>
        _entries = entries;

    public IEnumerable<EntitlementEntry> Entries => _entries.Values;

    public int Count => _entries.Count;

    public bool Contains(EntitlementKey key) => _entries.ContainsKey(key);

    public bool TryGet(EntitlementKey key, out EntitlementEntry entry) =>
        _entries.TryGetValue(key, out entry);

    /// <summary>The value for this key, falling back to the catalogue default when the set does not
    /// carry it. Enforcement sites read through here so that a key nobody has configured yet reads
    /// as its declared floor rather than throwing at a choke point.</summary>
    public EntitlementValue Value(EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key, out var entry) ? entry.Value : key.Default;
    }

    public bool Flag(EntitlementKey key) => Value(key).AsFlag;

    public long Number(EntitlementKey key) => Value(key).AsNumber;

    /// <summary>The rung name for a ladder key, which is what an enforcement site and a client
    /// actually want rather than a rank.</summary>
    public string Rung(EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Format(Value(key));
    }

    public EntitlementProvenance ProvenanceOf(EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key, out var entry)
            ? entry.Provenance
            : EntitlementProvenance.CatalogueDefault;
    }
}

/// <summary>Builds an <see cref="EntitlementSet"/> for one source.</summary>
public sealed class EntitlementSetBuilder
{
    private readonly Dictionary<EntitlementKey, EntitlementEntry> _entries = new();
    private readonly EntitlementPrecedence _source;

    public EntitlementSetBuilder(EntitlementPrecedence source)
    {
        if (source == EntitlementPrecedence.CatalogueDefault)
        {
            throw new ArgumentException(
                "CatalogueDefault is not a source. It is what the resolver credits when no source "
                + "supplied a key, and a source claiming it would make the provenance screen lie.",
                nameof(source));
        }

        _source = source;
    }

    /// <summary>Sets a key, merging with anything already set for it.</summary>
    public EntitlementSetBuilder Set(EntitlementKey key, EntitlementValue value, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (value.Kind != key.ValueKind)
        {
            throw new ArgumentException(
                $"Key '{key.Name}' is {key.ValueKind}; a {value.Kind} value was supplied.", nameof(value));
        }

        var merged = _entries.TryGetValue(key, out var existing)
            ? EntitlementValue.Merge(existing.Value, value)
            : value;

        _entries[key] = new EntitlementEntry(key, merged, new EntitlementProvenance(_source, detail));
        return this;
    }

    public EntitlementSetBuilder Flag(EntitlementKey key, bool granted, string? detail = null) =>
        Set(key, EntitlementValue.OfFlag(granted), detail);

    public EntitlementSetBuilder Number(EntitlementKey key, long limit, string? detail = null) =>
        Set(key, EntitlementValue.OfNumber(limit), detail);

    public EntitlementSetBuilder Rung(EntitlementKey key, string rung, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Set(key, key.Parse(rung), detail);
    }

    public EntitlementSet Build() => new(_entries);
}
