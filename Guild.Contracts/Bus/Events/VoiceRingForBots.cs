namespace Guild.Contracts.Bus.Events;

/// <summary>
/// A voice-channel ring was opened or closed - somebody in a voice channel asked one specific
/// member to join them, and later that invitation was accepted, declined, cancelled or ran out.
/// </summary>
public class VoiceRingForBots
{
    public string RingId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>The member who was already in the channel and sent the invitation.</summary>
    public string InviterId { get; set; } = null!;

    public string TargetUserId { get; set; } = null!;

    /// <summary>One of <c>Pending</c>, <c>Accepted</c>, <c>Declined</c>, <c>Cancelled</c>,
    /// <c>Expired</c> - the ring's state as of this message. <c>Pending</c> is the opening
    /// message.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Why a non-<c>Pending</c> status was reached, as one of the wire strings in
    /// <c>VoiceRingReason</c>. Null on <c>Pending</c>, and null for a plain accept or decline, where
    /// the status already says everything.</summary>
    public string? Reason { get; set; }

    public DateTime OccurredAt { get; set; }
}
