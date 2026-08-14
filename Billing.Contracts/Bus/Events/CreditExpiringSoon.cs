namespace Billing.Contracts.Bus.Events;

/// <summary>The realtime event names the credit surface pushes under.</summary>
public static class CreditRealtimeEvents
{
    /// <summary>A lot of the recipient's credit is within the warning window.</summary>
    public const string ExpiringSoon = "credit.ExpiringSoon";
}

/// <summary>
/// A parcel of somebody's promotional credit is about to lapse (monetization.md section 8.5, thirty
/// days out by default).
/// </summary>
public class CreditExpiringSoon
{
    public string UserId { get; set; } = null!;

    public string LotId { get; set; } = null!;

    /// <summary>What remained in the lot at the moment the warning was claimed.</summary>
    public long Points { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The campaign the lot came from, when there was one.</summary>
    public string? CampaignId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
