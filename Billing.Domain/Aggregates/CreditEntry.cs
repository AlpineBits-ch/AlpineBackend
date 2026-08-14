using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>What a ledger line is.</summary>
public enum CreditEntryKind
{
    /// <summary>Credit arriving.</summary>
    Issue,

    /// <summary>Credit consumed by a purchase.</summary>
    Spend,

    /// <summary>A lot reached its date. Negative, written by the sweep, never a delete.</summary>
    Expiry,

    /// <summary>Credit taken back - the fraud void of monetization.md section 8.6, or an issuance
    /// somebody made in error. Negative, and requires a reason.</summary>
    Reversal,

    /// <summary>A hand correction in either direction, for the cases the four above do not describe.
    /// Requires a reason.</summary>
    Adjustment,
}

/// <summary>
/// One line of the credit ledger, exactly as monetization.md section 8.5 specifies it.
/// </summary>
public class CreditEntry : BaseEntity<CreditEntry>, IPrefixedEntity
{
    public static string Prefix { get; } = "cred";

    /// <summary>Wallets are user-scoped and guilds never hold balances (section 8.4).</summary>
    public string UserId { get; set; } = null!;

    /// <summary>Signed.</summary>
    public long Amount { get; set; }

    public CreditEntryKind Kind { get; set; }

    /// <summary>Which lot this line belongs to.</summary>
    public string? LotId { get; set; }

    /// <summary>The campaign that issued it, when one did. Null for a hand issuance.</summary>
    public string? CampaignId { get; set; }

    /// <summary>The entitlement grant a spend produced.</summary>
    public string? GrantId { get; set; }

    /// <summary>Unique across the table. See the class comment.</summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>Required on <see cref="CreditEntryKind.Adjustment"/> and
    /// <see cref="CreditEntryKind.Reversal"/>, and enforced by a check constraint rather than only
    /// here: those two are the kinds a human chose to write, and a hand-written line with no
    /// explanation is a balance nobody can defend later.</summary>
    public string? Reason { get; set; }

    /// <summary>The staff account behind a hand-issued or hand-reversed line.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Whether this kind may not be written without a reason.</summary>
    public static bool RequiresReason(CreditEntryKind kind) =>
        kind is CreditEntryKind.Adjustment or CreditEntryKind.Reversal;
}
