namespace Guild.Application.Models;

/// <summary>Cache-key helpers for guild voice.</summary>
public static class ChannelVoiceState
{
    /// <summary>Where a user currently is, so a join elsewhere can evict them.</summary>
    public static string GetUserCacheKey(string userId) => $"voice:user:{userId}";
}

internal record UserVoiceLocation
{
    public string ChannelId { get; init; } = string.Empty;
    public string GuildId { get; init; } = string.Empty;
    public string? DeviceId { get; init; }
}
