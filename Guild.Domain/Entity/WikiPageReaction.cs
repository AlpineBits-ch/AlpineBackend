namespace Guild.Domain.Entity;

public class CreateWikiPageReactionParams
{
    public string PageId { get; init; }
    public string GuildId { get; init; }
    public string UserId { get; init; }
    public string Emoji { get; init; }
}

/// <summary>
/// One user's one emoji on one wiki page - the same grain as Messaging's Reaction, which is also a
/// keyless join row rather than an entity with its own identifier.
/// </summary>
public class WikiPageReaction
{
    public string PageId { get; set; }

    /// <summary>Denormalised from the page so guild-scoped reads and the cascade from a deleted
    /// guild do not need a join. Always equal to the owning page's GuildId.</summary>
    public string GuildId { get; set; }

    public string UserId { get; set; }
    public string Emoji { get; set; }
    public DateTime CreatedAt { get; set; }

    public static WikiPageReaction Create(CreateWikiPageReactionParams @params)
    {
        if (!EmojiText.IsSingleEmoji(@params.Emoji))
            throw new ArgumentException("Reaction must be a single emoji", nameof(@params.Emoji));

        return new WikiPageReaction
        {
            PageId = @params.PageId,
            GuildId = @params.GuildId,
            UserId = @params.UserId,
            Emoji = @params.Emoji,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
