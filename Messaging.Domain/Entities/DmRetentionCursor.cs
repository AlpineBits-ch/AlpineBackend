using Ids;
using Persistence;

namespace Messaging.Domain.Entities;

/// <summary>
/// Where the DM retention sweep got to, so the next tick starts after it instead of at the top.
/// </summary>
public class DmRetentionCursor : BaseEntity<DmRetentionCursor>, IPrefixedEntity
{
    public static string Prefix { get; } = "dmrc";

    /// <summary>The one row's id.</summary>
    public const string SingletonId = "dmrc_SWEEPPOSITION0000000000";

    /// <summary>
    /// Exclusive lower bound for the next page: the sweep reads ids strictly greater than this.
    /// </summary>
    public string LastUserId { get; set; } = string.Empty;

    /// <summary>When the current rotation started.</summary>
    public DateTimeOffset RotationStartedAt { get; set; }

    /// <summary>Completed full passes over the member set.</summary>
    public long RotationsCompleted { get; set; }

    /// <summary>Accounts examined so far in the current rotation.</summary>
    public int UsersSeenThisRotation { get; set; }

    /// <summary>Whether the slow-rotation warning has already been logged for this rotation. Without
    /// it, a rotation that has gone long warns on every single tick until it finishes - which for a
    /// six-hourly job is how an operator learns to filter the warning out.</summary>
    public bool LagWarningIssued { get; set; }
}
