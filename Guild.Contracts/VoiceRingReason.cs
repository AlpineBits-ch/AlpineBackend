namespace Guild.Contracts;

/// <summary>
/// Why a voice-channel ring stopped being pending, when the status alone does not say.
/// </summary>
public static class VoiceRingReason
{
    /// <summary>The inviter took it back before it was answered.</summary>
    public const string InviterCancelled = "InviterCancelled";

    /// <summary>The inviter left the voice channel they were inviting into.</summary>
    public const string InviterLeft = "InviterLeft";

    /// <summary>The same inviter rang the same person into a different channel.</summary>
    public const string Superseded = "Superseded";

    /// <summary>The target joined the channel by ordinary means while the ring was outstanding.
    /// Not an accept - they may never have seen the invitation - but the invitation has plainly got
    /// what it wanted, and leaving it on screen would ask them to join a room they are in.</summary>
    public const string TargetJoined = "TargetJoined";

    /// <summary>The channel was deleted, stopped being a voice channel, or the target lost the
    /// permission to see or connect to it while the ring was outstanding.</summary>
    public const string ChannelGone = "ChannelGone";

    /// <summary>Nobody answered before the ring's own deadline.</summary>
    public const string TimedOut = "TimedOut";
}
