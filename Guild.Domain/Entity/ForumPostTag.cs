namespace Guild.Domain.Entity;

/// <summary>
/// Join row between a forum post (a Channel of type Thread) and a <see cref="ForumTag"/>.
/// </summary>
public class ForumPostTag
{
    /// <summary>The post - a Channel with Type == Thread parented to a Forum/Media channel.</summary>
    public string ThreadChannelId { get; set; } = null!;

    public string TagId { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Matches Discord's cap on applied_tags.</summary>
    public const int MaxTagsPerPost = 5;
}
