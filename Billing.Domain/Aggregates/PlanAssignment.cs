using Echo.Entitlements.Model;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>Which plan version a subject is bound to.</summary>
public class PlanAssignment : BaseEntity<PlanAssignment>, IPrefixedEntity
{
    public static string Prefix { get; } = "plas";

    public SubjectKind SubjectKind { get; set; }

    /// <summary>An opaque guild or user id.</summary>
    public string SubjectId { get; set; } = null!;

    public string PlanId { get; set; } = null!;

    /// <summary>The pinned <see cref="PlanVersion.VersionNumber"/>.</summary>
    public int VersionNumber { get; set; }

    public string AssignedBy { get; set; } = null!;

    public string Reason { get; set; } = null!;

    public DateTimeOffset AssignedAt { get; set; }

    public EntitlementSubject Subject => new(SubjectKind, SubjectId);
}
