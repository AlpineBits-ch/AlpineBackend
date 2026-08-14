using Echo.Entitlements.Model;

namespace Echo.Entitlements.Keys;

/// <summary>The three value shapes an entitlement can have, and with them the three merge rules in
/// spec section 4.1. Adding a fourth means adding a rule to both
/// <see cref="EntitlementValue.Merge"/> and <see cref="EntitlementValue.Restrict"/>.</summary>
public enum EntitlementValueKind
{
    /// <summary>On or off. Merges across sources as a logical OR.</summary>
    Flag,

    /// <summary>A ceiling. Merges across sources as the maximum.</summary>
    Numeric,

    /// <summary>A position on an ordered <see cref="EntitlementLadder"/>.</summary>
    Ladder,
}

/// <summary>Which side of the relationship gets a say in a key's value.</summary>
public enum EntitlementScope
{
    /// <summary>Resolved from guild sources only.</summary>
    Guild,

    /// <summary>Resolved from user sources only.</summary>
    User,

    /// <summary>Both sides have a ceiling and the effective value is the lower of the two. The
    /// guild says what it will pay to distribute; the user says what they are allowed to produce.
    /// </summary>
    Paired,
}

/// <summary>
/// One entitlement key: a name, the shape of its value, whose sources may set it, and what it
/// resolves to when nothing sets it at all.
/// </summary>
public sealed class EntitlementKey : IEquatable<EntitlementKey>
{
    private EntitlementKey(
        string name,
        EntitlementValueKind valueKind,
        EntitlementScope scope,
        EntitlementValue defaultValue,
        EntitlementLadder? ladder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (defaultValue.Kind != valueKind)
        {
            throw new ArgumentException(
                $"Key '{name}' is declared {valueKind} but its default is a {defaultValue.Kind}.",
                nameof(defaultValue));
        }

        if (valueKind == EntitlementValueKind.Ladder && ladder is null)
        {
            throw new ArgumentException($"Ladder key '{name}' must declare its ladder.", nameof(ladder));
        }

        if (valueKind != EntitlementValueKind.Ladder && ladder is not null)
        {
            throw new ArgumentException($"Key '{name}' is not a ladder but was given one.", nameof(ladder));
        }

        Name = name;
        ValueKind = valueKind;
        Scope = scope;
        Default = defaultValue;
        Ladder = ladder;
    }

    /// <summary>Dotted, lowercase, and stable.</summary>
    public string Name { get; }

    public EntitlementValueKind ValueKind { get; }

    public EntitlementScope Scope { get; }

    /// <summary>What this key resolves to when no source supplies it.</summary>
    public EntitlementValue Default { get; }

    /// <summary>Non-null exactly when <see cref="ValueKind"/> is
    /// <see cref="EntitlementValueKind.Ladder"/>.</summary>
    public EntitlementLadder? Ladder { get; }

    public static EntitlementKey Flag(string name, EntitlementScope scope, bool defaultValue) =>
        new(name, EntitlementValueKind.Flag, scope, EntitlementValue.OfFlag(defaultValue), null);

    public static EntitlementKey Numeric(string name, EntitlementScope scope, long defaultValue) =>
        new(name, EntitlementValueKind.Numeric, scope, EntitlementValue.OfNumber(defaultValue), null);

    public static EntitlementKey OnLadder(
        string name, EntitlementScope scope, EntitlementLadder ladder, string defaultRung)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        return new EntitlementKey(
            name, EntitlementValueKind.Ladder, scope,
            EntitlementValue.OfRank(ladder.RankOf(defaultRung)), ladder);
    }

    /// <summary>True when a subject of this kind may contribute to this key.</summary>
    public bool AppliesTo(SubjectKind subject) => Scope switch
    {
        EntitlementScope.Guild => subject == SubjectKind.Guild,
        EntitlementScope.User => subject == SubjectKind.User,
        EntitlementScope.Paired => true,
        _ => false,
    };

    /// <summary>A value for this key parsed from its configuration or grant representation: a rung
    /// name for a ladder, <c>true</c>/<c>false</c> for a flag, and a number or <c>unlimited</c> for
    /// a numeric limit.</summary>
    public EntitlementValue Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();

        switch (ValueKind)
        {
            case EntitlementValueKind.Flag:
                return bool.TryParse(trimmed, out var flag)
                    ? EntitlementValue.OfFlag(flag)
                    : throw new FormatException($"Key '{Name}' is a flag; '{text}' is not true or false.");

            case EntitlementValueKind.Numeric:
                if (string.Equals(trimmed, "unlimited", StringComparison.OrdinalIgnoreCase))
                {
                    return EntitlementValue.OfNumber(EntitlementValue.Unlimited);
                }

                return long.TryParse(trimmed, out var number) && number >= 0
                    ? EntitlementValue.OfNumber(number)
                    : throw new FormatException(
                        $"Key '{Name}' is a numeric limit; '{text}' is not a non-negative number or 'unlimited'.");

            case EntitlementValueKind.Ladder:
                return EntitlementValue.OfRank(Ladder!.RankOf(trimmed));

            default:
                throw new InvalidOperationException($"Key '{Name}' has an unknown value kind {ValueKind}.");
        }
    }

    /// <summary>The value in the same representation <see cref="Parse"/> accepts, for the admin
    /// provenance screen and for round-tripping a grant.</summary>
    public string Format(EntitlementValue value) =>
        ValueKind == EntitlementValueKind.Ladder ? Ladder!.Describe(value) : value.ToString();

    public bool Equals(EntitlementKey? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EntitlementKey);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    public override string ToString() => Name;
}
