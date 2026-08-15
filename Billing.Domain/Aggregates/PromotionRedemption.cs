using Echo.Entitlements.Model;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>
/// One record of "this subject has had this campaign", keyed <c>(campaign, subject)</c> and
/// permanent.
/// </summary>
public class PromotionRedemption : BaseEntity<PromotionRedemption>, IPrefixedEntity
{
    public static string Prefix { get; } = "prdm";

    public string CampaignId { get; set; } = null!;

    /// <summary>Which subject this row is the record for.</summary>
    public SubjectKind SubjectKind { get; set; }

    /// <summary>An opaque user or guild id.</summary>
    public string SubjectId { get; set; } = null!;

    /// <summary>The account that took the offer, recorded on the guild rows too so that "who put this
    /// guild's trial on it" is answerable from the row that refused the second one.</summary>
    public string OwnerUserId { get; set; } = null!;

    public DateTimeOffset RedeemedAt { get; set; }

    /// <summary>When the conferred plan stops.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Stamped once <see cref="EndsAt"/> has passed.</summary>
    public DateTimeOffset? ExpiredAt { get; set; }

    /// <summary>Stamped on a guild row when the owner moves their trial elsewhere.</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>The Stripe subscription carrying the trial, on the owner row.</summary>
    public string? StripeSubscriptionId { get; set; }

    public bool IsExpiredAt(DateTimeOffset instant) => EndsAt is not null && EndsAt <= instant;

    public bool IsReleased => ReleasedAt is not null;

    public EntitlementSubject Subject => new(SubjectKind, SubjectId);
}

/// <summary>Which identity a mark was taken from.</summary>
public enum PromotionIdentityKind
{
    /// <summary>A client device id from the consolidated device set.</summary>
    Device,

    /// <summary>The number on the account.</summary>
    Phone,

    /// <summary>Stripe's <c>payment_method.card.fingerprint</c>, which is stable across accounts for
    /// the same physical card and is therefore the strongest control that actually exists.</summary>
    Card,
}

/// <summary>
/// A salted hash of one identity a redemption was made with, so a second account presenting the
/// same identity is recognised as a repeat.
/// </summary>
public class PromotionIdentityMark : BaseEntity<PromotionIdentityMark>, IPrefixedEntity
{
    public static string Prefix { get; } = "pmrk";

    public string CampaignId { get; set; } = null!;

    /// <summary>The redemption that produced it.</summary>
    public string RedemptionId { get; set; } = null!;

    public PromotionIdentityKind Kind { get; set; }

    /// <summary>Salted, keyed hash. See the type comment.</summary>
    public string Hash { get; set; } = null!;
}
