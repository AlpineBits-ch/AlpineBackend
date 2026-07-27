using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

public class VoiceStatePayload
{
    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }

    /// <summary>Null means the user left voice entirely.</summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>Synthesized, not a real WebRTC session - bots can't join voice channels in this
    /// system, so nothing actually depends on this being a real session identifier. Present only
    /// because Discord's real VoiceState object always has one.</summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("deaf")]
    public bool Deaf { get; set; }

    [JsonPropertyName("mute")]
    public bool Mute { get; set; }

    [JsonPropertyName("self_deaf")]
    public bool SelfDeaf { get; set; }

    [JsonPropertyName("self_mute")]
    public bool SelfMute { get; set; }

    [JsonPropertyName("self_stream")]
    public bool SelfStream { get; set; }

    [JsonPropertyName("self_video")]
    public bool SelfVideo { get; set; }
}
