using Echo.Voice.Rooms;

namespace Guild.Application.Models;

/// <summary>Cache-key helpers and the legacy response shape for guild voice.</summary>
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

/// <summary>The pre-unification response shape, kept so existing clients keep working.</summary>
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
    public static ChannelVoiceStateResponse From(VoiceRoom room) => new(
        room.RoomId,
        room.GuildId ?? string.Empty,
        room.Participants.Select(p => new VoiceStateResponse(
            p.UserId, room.RoomId, room.GuildId ?? string.Empty,
            p.IsSelfMuted, p.IsSelfDeafened,
            p.IsServerMuted, p.IsServerDeafened,
            p.IsStreaming, p.JoinedAt)).ToList());

    public static ChannelVoiceStateResponse Empty(string channelId, string guildId) =>
        new(channelId, guildId, []);
}
