namespace Guild.Domain.Enums;

/// <summary>What an invite is an invite to, beyond the guild itself.</summary>
public enum InviteTargetType
{
    /// <summary>An ordinary guild invite.</summary>
    None,

    /// <summary>Lands the redeemer in a specific voice channel.</summary>
    VoiceChannel,
}
