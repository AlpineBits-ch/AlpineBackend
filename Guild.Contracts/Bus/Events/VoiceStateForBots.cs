namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Unified voice-state-changed event - covers join/leave/move/mute/deafen/stream/video, matching
/// how Discord's own VOICE_STATE_UPDATE is a single event carrying the member's complete current
/// voice state rather than a per-field delta. Published with the full current state at every call
/// site that mutates Guild.Application.Models.VoiceState, not just the field that changed.
/// </summary>
public class VoiceStateForBots
{
    public string GuildId { get; set; }
    public string UserId { get; set; }

    /// <summary>Null means the user left voice entirely (not just muted/moved).</summary>
    public string? ChannelId { get; set; }

    public bool SelfMute { get; set; }
    public bool SelfDeaf { get; set; }
    public bool SelfStream { get; set; }
    public bool SelfVideo { get; set; }
    public bool Mute { get; set; }
    public bool Deaf { get; set; }
}
