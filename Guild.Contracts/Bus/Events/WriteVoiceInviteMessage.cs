namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Asks Messaging to put a voice-channel invitation in the two people's direct conversation.
/// </summary>
public class WriteVoiceInviteMessage
{
    /// <summary>The ring this invitation is the durable half of, when there is one.</summary>
    public string? RingId { get; set; }

    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>The channel's name at the moment of the invitation.</summary>
    public string ChannelName { get; set; } = "";

    /// <summary>Who is doing the asking.</summary>
    public string InviterId { get; set; } = null!;

    /// <summary>Who is being asked. The other half of the 1:1 conversation this lands in.</summary>
    public string TargetUserId { get; set; } = null!;

    /// <summary>
    /// When the ring stops being acceptable, or null when there is no ring and the invitation
    /// simply stands.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>What became of it.</summary>
public class WriteVoiceInviteMessageResponse
{
    public bool Written { get; set; }

    /// <summary>Where it landed - an existing conversation between the two, or one just opened for
    /// them. Handed back so a client can offer to jump to it.</summary>
    public string? ConversationId { get; set; }

    /// <summary>Why not, as one of a small set of wire strings.</summary>
    public string? Refusal { get; set; }

    public const string RecipientPolicy = "RecipientPolicy";
    public const string Unavailable = "Unavailable";
}
