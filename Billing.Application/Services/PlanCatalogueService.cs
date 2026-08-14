using System.Text.Json;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>
/// The plan catalogue as it actually is: the rows in this database, with the configured plans
/// underneath them.
/// </summary>
public sealed class PlanCatalogueService(
    MicroserviceContext db,
    PlanCatalogue configured,
    ILogger<PlanCatalogueService>? logger = null)
{
    /// <summary>Separates a plan name from a pinned version number.</summary>
    public const char VersionSeparator = '@';

    private PlanCatalogue? _resolved;

    /// <summary>What the instance would resolve with no rows at all.</summary>
    public PlanCatalogue Configured { get; } = configured;

    public static string Reference(string planName, int versionNumber) =>
        $"{planName}{VersionSeparator}{versionNumber}";

    /// <summary>Splits <c>pro@2</c> into its parts.</summary>
    public static (string Name, int? Version) SplitReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var at = reference.LastIndexOf(VersionSeparator);
        if (at <= 0 || at == reference.Length - 1) return (reference, null);

        return int.TryParse(reference.AsSpan(at + 1), out var version)
            ? (reference[..at], version)
            : (reference, null);
    }

    /// <summary>
    /// Every plan a grant may name or a subject may be on, current versions under their bare name
    /// and every live version under its <c>name@number</c> form.
    /// </summary>
    public async Task<PlanCatalogue> CurrentAsync(CancellationToken cancellationToken)
    {
        if (_resolved is not null) return _resolved;

        var plans = await db.Plans
            .AsNoTracking()
            .Select(plan => new { plan.Id, plan.Name, plan.CurrentVersionNumber, plan.ArchivedAt })
            .ToListAsync(cancellationToken);

        if (plans.Count == 0) return _resolved = Configured;

        var planIds = plans.Select(plan => plan.Id).ToList();

        var versions = await db.PlanVersions
            .AsNoTracking()
            .Where(version => planIds.Contains(version.PlanId) && version.ArchivedAt == null)
            .Select(version => new { version.PlanId, version.VersionNumber, version.ValuesJson })
            .ToListAsync(cancellationToken);

        var definitions = new List<PlanDefinition>();

        foreach (var plan in plans)
        {
            if (plan.ArchivedAt is not null) continue;

            foreach (var version in versions.Where(version => version.PlanId == plan.Id))
            {
                var values = ReadValues(version.ValuesJson);

                definitions.Add(Definition(Reference(plan.Name, version.VersionNumber), values));

                if (version.VersionNumber == plan.CurrentVersionNumber)
                {
                    definitions.Add(Definition(plan.Name, values));
                }
            }
        }

        // Configured plans the table has never heard of still resolve, so adding a plan to
        // configuration on an instance that already has rows works the way an operator expects.
        var known = plans.Select(plan => plan.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        definitions.AddRange(Configured.Plans.Where(plan => !known.Contains(plan.Name)));

        return _resolved = new PlanCatalogue(definitions);
    }

    /// <summary>The plan reference a subject is on - <c>pro@2</c> for a pinned subject, null for one
    /// that has never been assigned. Null is the normal answer for the overwhelming majority of
    /// subjects and means every key falls to whatever the other sources say.</summary>
    public async Task<string?> PlanReferenceForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var assignment = await AssignmentForAsync(subject, cancellationToken);
        if (assignment is null) return null;

        var name = await db.Plans
            .AsNoTracking()
            .Where(plan => plan.Id == assignment.PlanId)
            .Select(plan => plan.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return name is null ? null : Reference(name, assignment.VersionNumber);
    }

    /// <summary>
    /// The numbers a subject's own plan gives them, at the version they are pinned to.
    /// </summary>
    public async Task<PlanDefinition?> PlanForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var reference = await PlanReferenceForAsync(subject, cancellationToken);
        if (reference is null) return null;

        var catalogue = await CurrentAsync(cancellationToken);
        return catalogue.Find(reference);
    }

    public Task<PlanAssignment?> AssignmentForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        return db.PlanAssignments
            .FirstOrDefaultAsync(a => a.SubjectKind == kind && a.SubjectId == id, cancellationToken);
    }

    /// <summary>Drops the memoised catalogue.</summary>
    public void Invalidate() => _resolved = null;

    public static IReadOnlyDictionary<string, string> ReadValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private PlanDefinition Definition(string name, IReadOnlyDictionary<string, string> values)
    {
        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();

        foreach (var (keyName, text) in values)
        {
            if (!EntitlementKeys.TryGet(keyName, out var key))
            {
                logger?.LogWarning(
                    "Plan '{Plan}' names entitlement key '{Key}', which is not in the catalogue. "
                    + "Skipping it; the rest of the plan still resolves.", name, keyName);
                continue;
            }

            try
            {
                parsed[key] = key.Parse(text ?? string.Empty);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                logger?.LogWarning(exception,
                    "Plan '{Plan}' sets '{Key}' to a value it cannot take. Skipping the key.",
                    name, keyName);
            }
        }

        return new PlanDefinition(name, parsed);
    }
}
