namespace Guild.Application.Models;

/// <summary>Who is in one voice channel right now, as the guild-level index sees it.</summary>
public class ChannelVoiceActivity
{
    public List<string> UserIds { get; set; } = [];

    /// <summary>Subset of <see cref="UserIds"/> currently sharing a screen.</summary>
    public List<string> StreamerIds { get; set; } = [];
}

/// <summary>A per-guild index of live voice occupancy, keyed by channel.</summary>
public class GuildVoiceActivity
{
    public string GuildId { get; set; } = string.Empty;

    /// <summary>channelId -&gt; occupancy.</summary>
    public Dictionary<string, ChannelVoiceActivity> Channels { get; set; } = new();

    public static string GetCacheKey(string guildId) => $"voice:guild:{guildId}";
}

// ── HTTP response shapes ──────────────────────────────────────────────────────

public record GuildVoiceActivityChannelDto(
    string ChannelId,
    int ParticipantCount,
    List<string> UserIds,
    bool HasStream,
    List<string> StreamerIds);

public record GuildVoiceActivityDto(
    string GuildId,
    int ParticipantCount,
    bool HasStream,
    List<GuildVoiceActivityChannelDto> Channels);
