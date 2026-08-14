using Echo.Entitlements.Model;

namespace Echo.Entitlements.Resolution;

/// <summary>Which plan a subject is on.</summary>
public interface IPlanAssignment
{
    /// <summary>The plan name for this subject, or null when no plan applies.</summary>
    ValueTask<string?> PlanNameForAsync(EntitlementSubject subject, CancellationToken cancellationToken);
}

/// <summary>Puts every subject of a kind on one configured plan.</summary>
public sealed class FixedPlanAssignment(EntitlementPlanOptions options) : IPlanAssignment
{
    public ValueTask<string?> PlanNameForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken) =>
        ValueTask.FromResult(subject.Kind == SubjectKind.Guild
            ? options.DefaultGuildPlan
            : options.DefaultUserPlan);
}

/// <summary>The bottom of the order: whatever plan the subject is on, Free included.</summary>
public sealed class PlanDefaultEntitlementSource(PlanCatalogue plans, IPlanAssignment assignment)
    : IEntitlementSource
{
    public EntitlementPrecedence Precedence => EntitlementPrecedence.PlanDefault;

    public async Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var name = await assignment.PlanNameForAsync(subject, cancellationToken).ConfigureAwait(false);
        var plan = plans.Find(name);

        return plan?.ToSet(EntitlementPrecedence.PlanDefault) ?? EntitlementSet.Empty;
    }
}
