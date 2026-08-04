using Ids;
using Persistence;

namespace Messaging.Domain.Entities;

/// <summary>
/// Where the DM retention sweep got to, so the next tick starts after it instead of at the top.
///
/// <para><b>Why this row exists.</b> The sweep originally read "distinct member user ids,
/// <c>ORDER BY id</c>, <c>TAKE MaxUsersPerTick</c>" every tick with no cursor at all. On any
/// instance with more accounts than the per-tick cap, that swept the same first N ids forever and
/// never reached anyone after them: every user past the cap had asked for their messages to be
/// deleted after N days, and they never were. Silently. The cap was never the bug - the missing
/// rotation was.</para>
///
/// <para><b>One row, not one per user.</b> A per-user watermark would be a table that grows with the
/// member table and still needs a scan to find who is furthest behind. What the sweep actually needs
/// is a single position in one ordered scan that wraps: read ids strictly greater than
/// <see cref="LastUserId"/>, and when the scan runs off the end, reset to the beginning and count a
/// rotation. That reaches everybody in <c>ceil(users / MaxUsersPerTick)</c> ticks and needs exactly
/// one durable value to do it.</para>
///
/// <para><b>Wrapping is also what makes an id added mid-rotation safe.</b> A user whose id sorts
/// before the current position is not reached in the rotation they joined during - but the reset to
/// the start means they are reached in the next one, which is a bounded delay rather than the
/// permanent exclusion the un-cursored version had. Nothing is ever skipped twice.</para>
///
/// <para><b>The position advances even when a user's sweep threw.</b> Per-user failures are caught
/// and logged by the sweep; holding the cursor back on one of them would stall the rotation for
/// everyone behind it, which is the original defect wearing a different hat. The failed user is
/// retried on the next rotation.</para>
/// </summary>
public class DmRetentionCursor : BaseEntity<DmRetentionCursor>, IPrefixedEntity
{
    public static string Prefix { get; } = "dmrc";

    /// <summary>
    /// The one row's id. Fixed rather than minted: there is a single sweep position per deployment,
    /// and a constant is what lets any instance read and write it without a lookup by convention
    /// (e.g. "the newest row", which under two instances is two rows and two divergent positions).
    /// </summary>
    public const string SingletonId = "dmrc_SWEEPPOSITION0000000000";

    /// <summary>
    /// Exclusive lower bound for the next page: the sweep reads ids strictly greater than this.
    /// Empty string means "start of the rotation", which is before every real id because
    /// <see cref="Identifier"/> never mints an empty one.
    /// </summary>
    public string LastUserId { get; set; } = string.Empty;

    /// <summary>When the current rotation started. The rotation-lag warning is measured off this,
    /// not off <see cref="BaseEntity{T}.UpdatedAt"/>, which moves every tick.</summary>
    public DateTimeOffset RotationStartedAt { get; set; }

    /// <summary>Completed full passes over the member set. An operator watching this stall knows the
    /// sweep is not keeping up long before any individual user notices their retention window is not
    /// being honoured.</summary>
    public long RotationsCompleted { get; set; }

    /// <summary>Accounts examined so far in the current rotation. Reported in the
    /// rotation-completed log line so "it finished" carries how much it finished over.</summary>
    public int UsersSeenThisRotation { get; set; }

    /// <summary>Whether the slow-rotation warning has already been logged for this rotation. Without
    /// it, a rotation that has gone long warns on every single tick until it finishes - which for a
    /// six-hourly job is how an operator learns to filter the warning out.</summary>
    public bool LagWarningIssued { get; set; }
}
