namespace Identity.Contracts.Bus.Commands;

/// <summary>Where a credit issuance came from.</summary>
public enum CreditIssuedBy
{
    /// <summary>A member of staff, by hand, from the console.</summary>
    Staff,

    /// <summary>A campaign, which is still ultimately a staff decision but is not one person doing
    /// one thing to one account.</summary>
    Campaign,
}

/// <summary>Which of the three things a human did to a hand-made grant.</summary>
public enum EntitlementGrantChange
{
    Issued,

    /// <summary>An expiry was moved, in either direction, or the grant was made permanent.</summary>
    Amended,

    Revoked,
}

/// <summary>Somebody's promotional credit balance went up and they should be told.</summary>
public class CreditIssuedNotification
{
    public string UserId { get; set; } = null!;

    /// <summary>See <see cref="EntitlementGrantNotification.DedupeKey"/> - the same contract, and the
    /// same reason for it.</summary>
    public string DedupeKey { get; set; } = null!;

    /// <summary>Points.</summary>
    public long Points { get; set; }

    /// <summary>What the wallet holds after the issuance, so the mail can answer the obvious next
    /// question without the recipient opening the app.</summary>
    public long BalancePoints { get; set; }

    /// <summary>When this parcel lapses.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public CreditIssuedBy IssuedBy { get; set; }

    public string Disclaimer { get; set; } = null!;

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// A hand-made grant was issued, amended or revoked, and the person it is attached to should hear
/// it from us rather than notice it.
/// </summary>
public class EntitlementGrantNotification
{
    public string UserId { get; set; } = null!;

    /// <summary>The stable identity of the transition, not of the message.</summary>
    public string DedupeKey { get; set; } = null!;

    public EntitlementGrantChange Change { get; set; }

    /// <summary>The plan's display name when the grant names a plan; null when it names specific
    /// entitlement keys instead.</summary>
    public string? PlanDisplayName { get; set; }

    /// <summary>The entitlement keys, for a grant that names them rather than a plan.</summary>
    public List<string> Entitlements { get; set; } = [];

    /// <summary>When it runs out, or null for a permanent one.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Somebody moved themselves onto a more expensive plan and the charge that goes with it.
/// </summary>
public class PlanUpgradedNotification
{
    public string UserId { get; set; } = null!;

    /// <summary>See <see cref="EntitlementGrantNotification.DedupeKey"/>.</summary>
    public string DedupeKey { get; set; } = null!;

    public string PlanDisplayName { get; set; } = null!;

    public string PreviousPlanDisplayName { get; set; } = null!;

    /// <summary>The end of the period that is now paid for, when the subscription has one. It is the
    /// date the recipient will want, because it is when the new price is charged again.</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
