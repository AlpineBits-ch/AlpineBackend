using Echo.Entitlements.Model;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>Plan shorthand, or an explicit set of entitlement keys.</summary>
public enum GrantKind
{
    Plan,
    Entitlements,
}

/// <summary>
/// Who or what produced the grant, for the provenance screen and for the audit trail.
/// </summary>
public enum GrantSource
{
    Staff,
    Promotion,
    Boost,
    Migration,
}

/// <summary>
/// A staff-issued or campaign-issued entitlement override, per monetization.md section 6.
/// </summary>
public class Grant : BaseEntity<Grant>, IPrefixedEntity
{
    public static string Prefix { get; } = "gran";

    /// <summary>Reused from <c>Echo.Entitlements</c> rather than redeclared.</summary>
    public SubjectKind SubjectKind { get; set; }

    /// <summary>An opaque user or guild id.</summary>
    public string SubjectId { get; set; } = null!;

    public GrantKind GrantKind { get; set; }

    /// <summary>
    /// The plan name when <see cref="GrantKind"/> is <see cref="GrantKind.Plan"/>.
    /// </summary>
    public string? Plan { get; set; }

    /// <summary>The specific keys when <see cref="GrantKind"/> is
    /// <see cref="GrantKind.Entitlements"/>, as the key-name-to-string-value map the entitlement
    /// catalogue parses.</summary>
    public string? EntitlementsJson { get; set; }

    /// <summary>Null means permanent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Required, free text, and the reason the audit trail is worth having.</summary>
    public string Reason { get; set; } = null!;

    public GrantSource Source { get; set; }

    /// <summary>The staff user id that issued it.</summary>
    public string CreatedBy { get; set; } = null!;

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedBy { get; set; }

    public string? RevokeReason { get; set; }

    public EntitlementSubject Subject => new(SubjectKind, SubjectId);

    public bool IsRevoked => RevokedAt is not null;

    /// <summary>Null <see cref="ExpiresAt"/> is permanent and this is false forever.</summary>
    public bool HasExpiredAt(DateTimeOffset instant) => ExpiresAt is not null && ExpiresAt <= instant;

    /// <summary>Whether this grant contributes anything at a given instant.</summary>
    public bool IsActiveAt(DateTimeOffset instant) => !IsRevoked && !HasExpiredAt(instant);

    /// <summary>Ends a grant without ending the record of it.</summary>
    public void Revoke(string revokedBy, string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (IsRevoked)
        {
            throw new InvalidOperationException(
                $"Grant {Id} was already revoked at {RevokedAt:O}. Revoking it again would overwrite "
                + "who did it and why, which is the record the grant exists to keep.");
        }

        RevokedAt = at;
        RevokedBy = revokedBy;
        RevokeReason = reason;
    }
}
