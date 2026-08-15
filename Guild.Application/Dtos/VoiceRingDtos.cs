using Guild.Application.Models;

namespace Guild.Application.Dtos;

/// <summary>Who to ask into the voice channel named in the route.</summary>
public record RingVoiceChannelDto(string TargetUserId);

/// <summary>A ring as an HTTP caller sees it.</summary>
public record VoiceRingDto(
    string RingId,
    string GuildId,
    string ChannelId,
    string? ChannelName,
    string InviterId,
    string TargetUserId,
    string Status,
    string? Reason,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    int ExpiresInSeconds,
    string? ResolvedByDeviceId)
{
    public static VoiceRingDto From(VoiceRing ring, DateTime now, string? channelName = null) => new(
        ring.Id,
        ring.GuildId,
        ring.ChannelId,
        channelName,
        ring.InviterId,
        ring.TargetUserId,
        ring.Status.ToString(),
        ring.Reason,
        ring.CreatedAt,
        ring.ExpiresAt,
        (int)Math.Max(0, (ring.ExpiresAt - now).TotalSeconds),
        ring.ResolvedByDeviceId);
}

/// <summary>Why a ring was refused, in the one shape every refusal uses.</summary>
public record VoiceRingRefusalDto(string Reason, int RetryAfterSeconds);
