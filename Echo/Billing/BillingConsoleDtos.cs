using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Billing;

/// <summary>
/// One resolved key on the provenance screen: what it came out as, and which source is credited
/// with it.
/// </summary>
public sealed record EntitlementProvenanceEntryDto(
    string Key,
    string ValueKind,
    string Scope,
    string Value,
    string Default,
    string Source,
    string? Detail,
    bool IsCatalogueDefault)
{
    public static EntitlementProvenanceEntryDto From(EntitlementKey key, EntitlementSet set)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(set);

        var provenance = set.ProvenanceOf(key);

        return new EntitlementProvenanceEntryDto(
            key.Name,
            key.ValueKind.ToString(),
            key.Scope.ToString(),
            key.Format(set.Value(key)),
            key.Format(key.Default),
            provenance.Source.ToString(),
            provenance.Detail,
            provenance.Source == EntitlementPrecedence.CatalogueDefault);
    }
}

/// <summary>Every effective key for one subject, with the source that won each one.</summary>
public sealed record EntitlementProvenanceDto(
    string SubjectKind,
    string SubjectId,
    long? Version,
    bool VersionAvailable,
    string LicenseMode,
    bool BillingDeployed,
    DateTimeOffset ResolvedAt,
    IReadOnlyList<EntitlementProvenanceEntryDto> Entries);

/// <summary>One key as the plan editor needs it: its shape, whose sources may set it, what it falls
/// back to, and - for a ladder - the rungs it can take, lowest first.</summary>
public sealed record EntitlementKeyDto(
    string Name,
    string ValueKind,
    string Scope,
    string Default,
    IReadOnlyList<string>? Rungs)
{
    public static EntitlementKeyDto From(EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new EntitlementKeyDto(
            key.Name,
            key.ValueKind.ToString(),
            key.Scope.ToString(),
            key.Format(key.Default),
            key.Ladder?.Rungs);
    }
}

/// <summary>What the billing section needs before it can draw anything.</summary>
public sealed record BillingConsoleCatalogueDto(
    bool BillingDeployed,
    string LicenseMode,
    bool CanWrite,
    long MinimumVoiceParticipants,
    IReadOnlyList<EntitlementKeyDto> Keys,
    IReadOnlyList<string> SubjectKinds,
    IReadOnlyList<string> GrantKinds,
    IReadOnlyList<string> GrantSources);
