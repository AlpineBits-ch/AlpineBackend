using Echo.Entitlements.Model;

namespace Billing.Contracts.Bus.Events;

/// <summary>Why one subject's entitlements are being re-announced.</summary>
public enum EntitlementsChangedReason
{
    GrantIssued,

    /// <summary>An expiry was moved, in either direction, or a grant was made permanent.</summary>
    GrantAmended,

    GrantRevoked,

    /// <summary>Nobody wrote anything; a date simply passed.</summary>
    GrantExpired,

    /// <summary>
    /// A plan version became the plan's current one, whether by an edit or by a rollback.
    /// </summary>
    PlanVersionActivated,

    /// <summary>A subject was put on a plan version, or moved between versions.</summary>
    PlanAssignmentChanged,

    /// <summary>
    /// The mirror of <see cref="GrantExpired"/>: a queued grant's start date arrived and it began
    /// counting.
    /// </summary>
    GrantStarted,
}

/// <summary>
/// The <c>billing.EntitlementsChanged</c> event of monetization.md section 4.3: this subject's
/// resolved entitlements are no longer what you cached.
/// </summary>
public class EntitlementsChanged
{
    /// <summary>Reused from <c>Echo.Entitlements</c> rather than sent as a string, matching the
    /// <c>Grant</c> record. The consumers of this event are exactly the services that already
    /// reference that library to resolve entitlements at all, so the type costs them nothing and a
    /// string would let a producer and a cache key disagree about capitalisation.</summary>
    public SubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = null!;

    public EntitlementsChangedReason Reason { get; set; }

    /// <summary>The grant behind the change, for the audit trail and for the provenance screen. Null
    /// is allowed because later sources - a Stripe subscription, a boost - will raise this event with
    /// no grant involved.</summary>
    public string? GrantId { get; set; }

    /// <summary>
    /// This subject's entitlement version after the change, monotonic and never reused.
    /// </summary>
    public long Version { get; set; }

    /// <summary>Advisory, and allowed to be empty.</summary>
    public List<string> ChangedKeys { get; set; } = [];

    public DateTimeOffset OccurredAt { get; set; }
}
