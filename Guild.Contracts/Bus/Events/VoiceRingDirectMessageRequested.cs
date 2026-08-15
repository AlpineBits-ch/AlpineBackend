namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Asks Messaging to leave a voice invitation in the inviter and invitee's direct conversation.
/// </summary>
public class VoiceRingDirectMessageRequested
{
    /// <summary>The ring this invitation is the durable half of.</summary>
    public string RingId { get; set; } = null!;

    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>The channel's name at the moment of the invitation.</summary>
    public string ChannelName { get; set; } = "";

    /// <summary>Who is doing the asking.</summary>
    public string InviterId { get; set; } = null!;

    /// <summary>Who is being asked. The other half of the 1:1 conversation this lands in.</summary>
    public string TargetUserId { get; set; } = null!;

    /// <summary>When the ring stops being acceptable.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
