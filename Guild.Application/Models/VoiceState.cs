namespace Guild.Application.Models;

public class ActiveScreenShare
{
    public string ShareId { get; set; } = string.Empty;
    public List<string> TrackNames { get; set; } = [];
}

public class VoiceState
{
    public string UserId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string? CfSessionId { get; set; }
    public string? AudioTrackName { get; set; }
    public bool IsSelfMuted { get; set; }
    public bool IsSelfDeafened { get; set; }
    public bool IsServerMuted { get; set; }
    public bool IsServerDeafened { get; set; }
    public bool IsStreaming { get; set; }
    public List<ActiveScreenShare> ActiveScreenShares { get; set; } = [];
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class ChannelVoiceState
{
    public string ChannelId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public List<VoiceState> Participants { get; set; } = [];

    public static string GetCacheKey(string channelId) => $"voice:channel:{channelId}";
    public static string GetUserCacheKey(string userId) => $"voice:user:{userId}";
    public static string GetHeartbeatCacheKey(string userId) => $"voice:heartbeat:{userId}";
}

// HTTP response DTO — omits cfSessionId/audioTrackName so clients
// discover them only via ParticipantJoined events (prevents pulling
// remote tracks before pushing local, which causes Cloudflare 425).
public record VoiceStateResponse(
    string UserId,
    string ChannelId,
    string GuildId,
    bool IsSelfMuted,
    bool IsSelfDeafened,
    bool IsServerMuted,
    bool IsServerDeafened,
    bool IsStreaming,
    DateTime JoinedAt);

public record ChannelVoiceStateResponse(
    string ChannelId,
    string GuildId,
    List<VoiceStateResponse> Participants)
{
    public static ChannelVoiceStateResponse From(ChannelVoiceState s) => new(
        s.ChannelId,
        s.GuildId,
        s.Participants.Select(p => new VoiceStateResponse(
            p.UserId, p.ChannelId, p.GuildId,
            p.IsSelfMuted, p.IsSelfDeafened,
            p.IsServerMuted, p.IsServerDeafened,
            p.IsStreaming, p.JoinedAt)).ToList());
}
