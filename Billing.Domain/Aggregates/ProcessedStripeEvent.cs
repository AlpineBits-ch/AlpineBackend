namespace Billing.Domain.Aggregates;

/// <summary>One Stripe event id, recorded before the event is handled.</summary>
public class ProcessedStripeEvent
{
    /// <summary><c>evt_...</c>, exactly as Stripe sent it.</summary>
    public string EventId { get; set; } = null!;

    /// <summary><c>customer.subscription.updated</c> and friends.</summary>
    public string Type { get; set; } = null!;

    /// <summary>When the row was inserted, which is before the event was handled.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Null until handling finishes.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>What handling concluded, free text, for the same reason a grant carries a reason.
    /// Null while in flight.</summary>
    public string? Outcome { get; set; }
}
