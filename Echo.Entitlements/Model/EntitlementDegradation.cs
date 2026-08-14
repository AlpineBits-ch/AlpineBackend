using Echo.Entitlements.Keys;

namespace Echo.Entitlements.Model;

/// <summary>Why a request was reduced.</summary>
public enum EntitlementDegradationReason
{
    /// <summary>The guild's plan is the binding constraint.</summary>
    GuildPlanLimit,

    /// <summary>The member's own plan is the binding constraint, so pointing them at the guild
    /// owner would be wrong.</summary>
    UserPlanLimit,

    /// <summary>A paired key where the two sides disagreed and the lower won.</summary>
    PairedCeiling,

    /// <summary>The operator's own env-level cap, which clamps below any entitlement.</summary>
    OperatorCeiling,
}

/// <summary>
/// What an enforcement site returns when it reduced a request instead of refusing it.
/// </summary>
/// <param name="Key">The entitlement key that bound.</param>
/// <param name="Requested">What was asked for, in the key's own representation.</param>
/// <param name="Granted">What was actually allowed.</param>
/// <param name="Reason">Which side bound.</param>
/// <param name="Detail">Optional free text for the admin console.</param>
public sealed record EntitlementDegradation(
    string Key,
    string Requested,
    string Granted,
    EntitlementDegradationReason Reason,
    string? Detail = null)
{
    /// <summary>
    /// The degradation for a request against an effective ceiling, or null when the request fits.
    /// </summary>
    public static EntitlementDegradation? IfReduced(
        EntitlementKey key,
        EntitlementValue requested,
        EntitlementValue allowed,
        EntitlementDegradationReason reason,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (requested.Kind != key.ValueKind || allowed.Kind != key.ValueKind)
        {
            throw new ArgumentException($"Key '{key.Name}' is {key.ValueKind}; a differently shaped value was supplied.");
        }

        return allowed.Raw >= requested.Raw
            ? null
            : new EntitlementDegradation(
                key.Name, key.Format(requested), key.Format(allowed), reason, detail);
    }
}
