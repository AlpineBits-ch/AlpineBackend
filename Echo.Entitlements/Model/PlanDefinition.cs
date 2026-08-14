using Echo.Entitlements.Keys;

namespace Echo.Entitlements.Model;

/// <summary>
/// A named bundle of key values - Free, Plus, Pro, Venta Plus, or whatever a later campaign needs.
/// </summary>
public sealed class PlanDefinition
{
    public PlanDefinition(
        string name,
        IReadOnlyDictionary<EntitlementKey, EntitlementValue> values,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
        {
            if (value.Kind != key.ValueKind)
            {
                throw new ArgumentException(
                    $"Plan '{name}' sets '{key.Name}' to a {value.Kind} value, but that key is {key.ValueKind}.",
                    nameof(values));
            }
        }

        Name = name;
        Values = values;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
    }

    public string Name { get; }

    /// <summary>What a settings screen calls this plan.</summary>
    public string DisplayName { get; }

    public IReadOnlyDictionary<EntitlementKey, EntitlementValue> Values { get; }

    /// <summary>Builds a plan from the string form configuration arrives in.</summary>
    public static PlanDefinition Parse(
        string name, IReadOnlyDictionary<string, string> values, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();
        foreach (var (keyName, text) in values)
        {
            var key = EntitlementKeys.Require(keyName);
            parsed[key] = key.Parse(text);
        }

        return new PlanDefinition(name, parsed, displayName);
    }

    public EntitlementSet ToSet(EntitlementPrecedence precedence = EntitlementPrecedence.PlanDefault)
    {
        var builder = new EntitlementSetBuilder(precedence);
        foreach (var (key, value) in Values) builder.Set(key, value, Name);
        return builder.Build();
    }
}

/// <summary>The configured plans, looked up by name.</summary>
public sealed class PlanCatalogue
{
    public static readonly PlanCatalogue Empty = new([]);

    private readonly Dictionary<string, PlanDefinition> _byName;

    public PlanCatalogue(IReadOnlyCollection<PlanDefinition> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        _byName = plans.ToDictionary(plan => plan.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<PlanDefinition> Plans => _byName.Values;

    public PlanDefinition? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out var plan) ? plan : null;

    /// <summary>
    /// The version of <paramref name="name"/> this catalogue publishes as current, or null when it
    /// publishes no versioned entries for it.
    /// </summary>
    public int? CurrentVersionOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        int? highest = null;

        foreach (var plan in _byName.Values)
        {
            var (planName, version) = PlanReference.Split(plan.Name);
            if (version is null) continue;
            if (!string.Equals(planName, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (highest is null || version > highest) highest = version;
        }

        return highest;
    }

    public static PlanCatalogue FromOptions(EntitlementPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PlanCatalogue(
            options.Plans
                .Select(entry => PlanDefinition.Parse(
                    entry.Key, entry.Value, options.PlanDisplayNames.GetValueOrDefault(entry.Key)))
                .ToList());
    }
}

/// <summary>The <c>name@number</c> form a pinned subject's plan is addressed by.</summary>
public static class PlanReference
{
    /// <summary>Refused inside a plan name, or a plan called <c>pro@2</c> would shadow version 2 of
    /// <c>pro</c>.</summary>
    public const char VersionSeparator = '@';

    public static string Of(string planName, int versionNumber) =>
        $"{planName}{VersionSeparator}{versionNumber}";

    /// <summary>Splits <c>pro@2</c> into its parts.</summary>
    public static (string Name, int? Version) Split(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var at = reference.LastIndexOf(VersionSeparator);
        if (at <= 0 || at == reference.Length - 1) return (reference, null);

        return int.TryParse(reference.AsSpan(at + 1), out var version)
            ? (reference[..at], version)
            : (reference, null);
    }
}

/// <summary>The bindable shape of the plan configuration.</summary>
public sealed class EntitlementPlanOptions
{
    public const string SectionName = "Entitlements";

    /// <summary>The plan a subject is on when nothing says otherwise, by name.</summary>
    public string? DefaultGuildPlan { get; set; }

    /// <summary>The user-side counterpart.</summary>
    public string? DefaultUserPlan { get; set; }

    public Dictionary<string, Dictionary<string, string>> Plans { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each plan is called on a settings screen, by plan name.</summary>
    public Dictionary<string, string> PlanDisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
