using Guild.Domain.Enums;

namespace Guild.Domain.Entity;

/// <summary>
/// Per-forum settings, 1:1 with a Forum/Media channel and keyed by its id (same shape as
/// GuildAutoModConfig/GuildOnboardingConfig).
/// </summary>
public class ForumConfig
{
    /// <summary>PK and FK both - the Forum/Media channel this configures.</summary>
    public string ChannelId { get; set; } = null!;

    public string GuildId { get; set; } = null!;

    /// <summary>Posts must carry at least one tag.</summary>
    public bool RequireTag { get; set; }

    public ForumSortOrder DefaultSortOrder { get; set; } = ForumSortOrder.LatestActivity;

    public ForumLayout DefaultLayout { get; set; } = ForumLayout.List;

    /// <summary>Stored and echoed for the client's one-tap reaction affordance; the backend never
    /// acts on it (a post has no reactions of its own - only the messages inside it do).</summary>
    public string? DefaultReactionEmojiId { get; set; }
    public string? DefaultReactionEmojiName { get; set; }

    /// <summary>Copied onto each new post at creation. Changing it doesn't touch existing posts.</summary>
    public int DefaultThreadSlowModeSeconds { get; set; }

    /// <summary>One of the four durations Discord allows: 60 (1h), 1440 (1d), 4320 (3d), 10080 (7d).</summary>
    public int DefaultAutoArchiveMinutes { get; set; } = DefaultAutoArchiveMinutesFallback;

    public const int DefaultAutoArchiveMinutesFallback = 4320;

    public static readonly int[] AllowedAutoArchiveMinutes = [60, 1440, 4320, 10080];

    /// <summary>The row a forum behaves as before anyone has configured it.</summary>
    public static ForumConfig Default(string channelId, string guildId) => new()
    {
        ChannelId = channelId,
        GuildId = guildId,
    };
}
