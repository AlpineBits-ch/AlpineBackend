using Echo.Entitlements.Model;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>Stripe's subscription vocabulary, mirrored rather than invented.</summary>
public enum SubscriptionStatus
{
    /// <summary>Stripe sent something this build has never heard of.</summary>
    Unknown,

    Incomplete,
    IncompleteExpired,
    Trialing,
    Active,
    PastDue,
    Canceled,
    Unpaid,
    Paused,
}

/// <summary>The translation between Stripe's wire strings and <see cref="SubscriptionStatus"/>, in
/// one place so the webhook path, the checkout path and the reconciler cannot disagree.</summary>
public static class SubscriptionStatuses
{
    /// <summary>The statuses that keep a subject on the plan they bought.</summary>
    public static readonly IReadOnlyList<SubscriptionStatus> Live =
    [
        SubscriptionStatus.Trialing,
        SubscriptionStatus.Active,
        SubscriptionStatus.PastDue,
    ];

    /// <summary>
    /// Maps a Stripe status string onto the enum, answering <see
    /// cref="SubscriptionStatus.Unknown"/> for anything it does not recognise rather than throwing.
    /// </summary>
    public static SubscriptionStatus Parse(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "incomplete" => SubscriptionStatus.Incomplete,
        "incomplete_expired" => SubscriptionStatus.IncompleteExpired,
        "trialing" => SubscriptionStatus.Trialing,
        "active" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "canceled" => SubscriptionStatus.Canceled,
        "unpaid" => SubscriptionStatus.Unpaid,
        "paused" => SubscriptionStatus.Paused,
        _ => SubscriptionStatus.Unknown,
    };

    public static bool IsLive(SubscriptionStatus status) => Live.Contains(status);
}

/// <summary>
/// The commercial record of one recurring relationship: who is paying, for what, and what Stripe
/// currently says about it.
/// </summary>
public class Subscription : BaseEntity<Subscription>, IPrefixedEntity
{
    public static string Prefix { get; } = "subs";

    /// <summary><c>sub_...</c>.</summary>
    public string StripeSubscriptionId { get; set; } = null!;

    /// <summary>The account being charged, matching <see cref="StripeCustomer.UserId"/>.</summary>
    public string PayerUserId { get; set; } = null!;

    /// <summary>What is being paid for: a guild for a guild plan, the payer themselves for a user
    /// plan. The same opaque cross-service ids the rest of Billing uses, with no foreign key for the
    /// same reason <see cref="Grant.SubjectId"/> has none.</summary>
    public SubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = null!;

    /// <summary>The pinned plan version, resolved from the Stripe price id rather than from anything
    /// the client sent. A <see cref="PlanVersion"/> and a Stripe Price are both immutable once
    /// created, so this pair is what says which numbers were bought and stays true after the plan is
    /// edited underneath it.</summary>
    public string PlanId { get; set; } = null!;

    public int VersionNumber { get; set; }

    public SubscriptionStatus Status { get; set; }

    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    public bool CancelAtPeriodEnd { get; set; }

    public string? LatestInvoiceId { get; set; }

    /// <summary>When the dunning grace after a failed payment runs out, or null when nothing is owed.
    /// Set by <c>invoice.payment_failed</c> and cleared by <c>invoice.paid</c>; the downgrade itself
    /// is a sweep, because nothing arrives from Stripe at the moment a grace period ends.</summary>
    public DateTimeOffset? GracePeriodEndsAt { get; set; }

    /// <summary>The <c>created</c> of the most recent Stripe event applied, for observability only.
    /// <b>It orders nothing.</b> Ordering by it would reintroduce exactly the out-of-order downgrade
    /// that re-reading the live object exists to prevent.</summary>
    public DateTimeOffset? LastEventAt { get; set; }

    public EntitlementSubject Subject => new(SubjectKind, SubjectId);

    /// <summary>Whether this subscription still keeps its subject on the plan.</summary>
    public bool IsLive => SubscriptionStatuses.IsLive(Status);

    /// <summary>Copies what Stripe currently says onto this row.</summary>
    public void MirrorFromStripe(
        SubscriptionStatus status,
        DateTimeOffset? currentPeriodEnd,
        bool cancelAtPeriodEnd,
        string? latestInvoiceId,
        DateTimeOffset? eventAt = null)
    {
        Status = status;
        CurrentPeriodEnd = currentPeriodEnd;
        CancelAtPeriodEnd = cancelAtPeriodEnd;
        LatestInvoiceId = latestInvoiceId;

        if (eventAt is not null) LastEventAt = eventAt;
    }
}
