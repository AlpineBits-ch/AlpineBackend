using Echo.Entitlements.Model;

namespace Billing.Contracts.Bus.Request;

/// <summary>"Which plan version is this subject pinned to?"</summary>
public class GetPlanAssignmentRequest
{
    public SubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = null!;
}

/// <summary>
/// The plan reference a subject is pinned to, or null for one that has never been assigned.
/// </summary>
public class GetPlanAssignmentResponse
{
    /// <summary><c>pro@2</c>: the plan name and the pinned version, in the form
    /// <c>PlanCatalogue</c> looks up. Null when the subject has no assignment row.</summary>
    public string? PlanReference { get; set; }
}
