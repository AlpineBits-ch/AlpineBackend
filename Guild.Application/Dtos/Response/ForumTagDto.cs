namespace Guild.Application.Dtos.Response;

/// <summary>Hand-written rather than Facet-generated: PostCount has no counterpart on the entity
/// (it's an aggregate computed per request), and the DTO is flat enough that the generator would
/// buy nothing.</summary>
public class ForumTagDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public string Color { get; set; } = null!;
    public int Position { get; set; }
    public bool Moderated { get; set; }

    /// <summary>Non-archived posts currently carrying this tag.</summary>
    public int PostCount { get; set; }
}
