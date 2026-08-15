namespace Guild.Contracts.Bus.Events;

/// <summary>
/// A phone notification for a voice-channel ring - somebody already sitting in a voice channel has
/// asked one specific person to come and join them.
/// </summary>
public class VoiceRingPushRequested
{
    /// <summary>The ring this notification is about.</summary>
    public string RingId { get; set; } = null!;

    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>Who is being rung.</summary>
    public string TargetUserId { get; set; } = null!;

    /// <summary>Who is doing the ringing, so the client can resolve a fresher name and an avatar
    /// itself rather than living with whatever this payload froze.</summary>
    public string InviterId { get; set; } = null!;

    public string? InviterAvatarUrl { get; set; }

    /// <summary>Seconds left before the ring expires on its own, as of publication.</summary>
    public int ExpiresInSeconds { get; set; }

    /// <summary>Notification title in English - the inviter's display name.</summary>
    public string Title { get; set; } = null!;

    public string Body { get; set; } = "";

    /// <summary>The localization key <see cref="Body"/> is the English rendering of.</summary>
    public string? BodyLocKey { get; set; }

    /// <summary>Values for the key's placeholders, in order, already formatted for display.</summary>
    public List<string> BodyLocArgs { get; set; } = [];

    /// <summary>
    /// True when this is the "stop showing that ring" push rather than the ring itself.
    /// </summary>
    public bool Cancel { get; set; }

    /// <summary>Cancel pushes only: why the ring stopped, as one of the wire strings the clients
    /// switch on. See <c>VoiceRingReason</c>.</summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// Cancel pushes only: the device that resolved the ring, echoed back so that device can ignore
    /// its own cancel.
    /// </summary>
    public string? ExcludeDeviceId { get; set; }
}
