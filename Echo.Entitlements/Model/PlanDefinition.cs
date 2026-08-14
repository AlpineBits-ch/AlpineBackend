using Echo.Entitlements.Keys;

namespace Echo.Entitlements.Model;

/// <summary>
/// A named bundle of key values - Free, Plus, Pro, Venta Plus, or whatever a later campaign needs.
/// </summary>
public sealed class PlanDefinition
{
    public PlanDefinition(string name, IReadOnlyDictionary<EntitlementKey, EntitlementValue> values)
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
    }

    public string Name { get; }

    public IReadOnlyDictionary<EntitlementKey, EntitlementValue> Values { get; }

    /// <summary>Builds a plan from the string form configuration arrives in.</summary>
    public static PlanDefinition Parse(string name, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();
        foreach (var (keyName, text) in values)
        {
            var key = EntitlementKeys.Require(keyName);
            parsed[key] = key.Parse(text);
        }

        return new PlanDefinition(name, parsed);
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

    public static PlanCatalogue FromOptions(EntitlementPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PlanCatalogue(
            options.Plans.Select(entry => PlanDefinition.Parse(entry.Key, entry.Value)).ToList());
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
}
