using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

public class ForumConfigDto
{
    public string ChannelId { get; set; } = null!;
    public bool RequireTag { get; set; }
    public ForumSortOrder DefaultSortOrder { get; set; }
    public ForumLayout DefaultLayout { get; set; }
    public string? DefaultReactionEmojiId { get; set; }
    public string? DefaultReactionEmojiName { get; set; }
    public int DefaultThreadSlowModeSeconds { get; set; }
    public int DefaultAutoArchiveMinutes { get; set; }

    public static ForumConfigDto From(ForumConfig config) => new()
    {
        ChannelId = config.ChannelId,
        RequireTag = config.RequireTag,
        DefaultSortOrder = config.DefaultSortOrder,
        DefaultLayout = config.DefaultLayout,
        DefaultReactionEmojiId = config.DefaultReactionEmojiId,
        DefaultReactionEmojiName = config.DefaultReactionEmojiName,
        DefaultThreadSlowModeSeconds = config.DefaultThreadSlowModeSeconds,
        DefaultAutoArchiveMinutes = config.DefaultAutoArchiveMinutes,
    };
}
