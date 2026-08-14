using Echo.Entitlements.Model;

namespace Echo.Entitlements.Resolution;

/// <summary>Which plan a subject is on.</summary>
public interface IPlanAssignment
{
    /// <summary>The plan name for this subject, or null when no plan applies.</summary>
    ValueTask<string?> PlanNameForAsync(EntitlementSubject subject, CancellationToken cancellationToken);
}

/// <summary>
/// Puts every subject of a kind on the plan this instance configured as its default.
/// </summary>
public sealed class FixedPlanAssignment(EntitlementPlanOptions options) : IPlanAssignment
{
    public ValueTask<string?> PlanNameForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefaultFor(subject.Kind));

    public string? DefaultFor(SubjectKind kind) =>
        kind == SubjectKind.Guild ? options.DefaultGuildPlan : options.DefaultUserPlan;
}

/// <summary>The bottom of the order: whatever plan the subject is on, Free included.</summary>
public sealed class PlanDefaultEntitlementSource : IEntitlementSource
{
    private readonly IPlanCatalogueSource _plans;
    private readonly IPlanAssignment _assignment;

    public PlanDefaultEntitlementSource(IPlanCatalogueSource plans, IPlanAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(assignment);

        _plans = plans;
        _assignment = assignment;
    }

    /// <summary>The fixed-catalogue form, for a caller that holds the plans rather than a source for
    /// them. Every configuration-only instance is this case.</summary>
    public PlanDefaultEntitlementSource(PlanCatalogue plans, IPlanAssignment assignment)
        : this(new FixedPlanCatalogueSource(plans), assignment)
    {
    }

    public EntitlementPrecedence Precedence => EntitlementPrecedence.PlanDefault;

    public async Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var name = await _assignment.PlanNameForAsync(subject, cancellationToken).ConfigureAwait(false);
        if (name is null) return EntitlementSet.Empty;

        var plans = await _plans.CurrentAsync(cancellationToken).ConfigureAwait(false);
        var plan = plans.Find(name);

        return plan?.ToSet(EntitlementPrecedence.PlanDefault) ?? EntitlementSet.Empty;
    }
}
