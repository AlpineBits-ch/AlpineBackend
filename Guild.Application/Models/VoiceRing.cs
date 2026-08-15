namespace Guild.Application.Models;

public enum VoiceRingStatus
{
    Pending,
    Accepted,
    Declined,
    Cancelled,
    Expired,
}

/// <summary>
/// One outstanding "come and join me in here", from a member sitting in a voice channel to a member
/// who is not.
/// </summary>
public class VoiceRing
{
    /// <summary>How long a target has to answer before the ring lapses on its own.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>How long the Redis entries live.</summary>
    public static readonly TimeSpan RetentionTtl = TimeSpan.FromMinutes(5);

    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string InviterId { get; set; } = null!;
    public string TargetUserId { get; set; } = null!;

    /// <summary>Which of the inviter's devices sent it, when it said.</summary>
    public string? InviterDeviceId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public VoiceRingStatus Status { get; set; } = VoiceRingStatus.Pending;

    /// <summary>One of <see cref="Guild.Contracts.VoiceRingReason"/>, or null when the status says
    /// everything.</summary>
    public string? Reason { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Which of the target's devices answered, when one did.</summary>
    public string? ResolvedByDeviceId { get; set; }

    /// <summary>Whether this ring is still live as of <paramref name="now"/>.</summary>
    public bool IsPending(DateTime now) => Status == VoiceRingStatus.Pending && ExpiresAt > now;

    public static string GenerateId() => $"ring_{Guid.NewGuid():N}";

    /// <summary>Where the ring itself lives.</summary>
    public static string CacheKey(string ringId) => $"voice:ring:{ringId}";

    /// <summary>
    /// Which rings target this user - the answer to "is anybody asking me into a channel right
    /// now".
    /// </summary>
    public static string TargetIndexKey(string userId) => $"voice:ring:user:{userId}";

    /// <summary>
    /// Which rings are outstanding into this channel - the answer to "does anything need cancelling
    /// because of what just happened here".
    /// </summary>
    public static string ChannelIndexKey(string channelId) => $"voice:ring:channel:{channelId}";
}

/// <summary>A set of ring ids under one index key.</summary>
public class VoiceRingIndex
{
    public List<string> RingIds { get; set; } = [];
}
