using Guild.Domain.Aggregates;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateGuildEmojiParams
{
    public string GuildId { get; set; }
    public string Name { get; set; }
    public string CreatedByUserId { get; set; }
    public bool Animated { get; set; }
}

public class GuildEmoji : BaseEntity<GuildEmoji>, IPrefixedEntity
{
    public static string Prefix { get; } = "emoj";

    public string GuildId { get; set; }
    public virtual Aggregates.Guild Guild { get; set; }

    /// <summary>Shortcode-style name, unique per guild (case-insensitive) - e.g. "pepega", used
    /// both as the :name: fallback text and, combined with GuildId, as the S3 object key.</summary>
    public string Name { get; set; }

    public string CreatedByUserId { get; set; }
    public bool Animated { get; set; }

    public static GuildEmoji Create(CreateGuildEmojiParams parameters)
    {
        var date = DateTime.UtcNow;
        return new GuildEmoji
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = parameters.GuildId,
            Name = parameters.Name,
            CreatedByUserId = parameters.CreatedByUserId,
            Animated = parameters.Animated,
        };
    }
}
