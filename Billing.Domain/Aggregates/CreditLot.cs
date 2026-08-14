using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>A parcel of credit with its own expiry date.</summary>
public class CreditLot : BaseEntity<CreditLot>, IPrefixedEntity
{
    public static string Prefix { get; } = "clot";

    public string UserId { get; set; } = null!;

    /// <summary>What the lot was worth when it was issued, in points. Always positive.</summary>
    public long OriginalAmount { get; set; }

    /// <summary>When what is left of it lapses.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The campaign that produced it, when one did.</summary>
    public string? CampaignId { get; set; }

    /// <summary>When the 30-day-out warning was sent, or null if it has not been.</summary>
    public DateTimeOffset? ExpiryWarningSentAt { get; set; }
}

/// <summary>
/// One row per user, and it is two things: the lock the spend transaction takes, and a cache of the
/// balance for reads.
/// </summary>
public class CreditWallet : BaseEntity<CreditWallet>, IPrefixedEntity
{
    public static string Prefix { get; } = "cwal";

    public string UserId { get; set; } = null!;

    /// <summary>A cache. See the class comment.</summary>
    public long CachedBalance { get; set; }

    /// <summary>When the cache last agreed with the entries by construction.</summary>
    public DateTimeOffset CachedAt { get; set; }
}
