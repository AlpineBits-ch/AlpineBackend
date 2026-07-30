using FluentValidation;
using Guild.Domain.Validators;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateForumTagParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public string? Color { get; set; }
    public int Position { get; set; }
    public bool Moderated { get; set; }
}

/// <summary>
/// A label defined on a single Forum/Media channel and applied to that forum's posts (see <see
/// cref="ForumPostTag"/>).
/// </summary>
public class ForumTag : BaseEntity<ForumTag>, IPrefixedEntity
{
    public static string Prefix { get; } = "ftag";

    /// <summary>The Forum/Media channel this tag belongs to.</summary>
    public string ChannelId { get; set; } = null!;

    /// <summary>Denormalized from the parent channel so authorization and audit logging don't need
    /// a join back to Channel on every tag write.</summary>
    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>A guild custom emoji id.</summary>
    public string? EmojiId { get; set; }

    /// <summary>A unicode emoji ("🐛"). Mutually exclusive with <see cref="EmojiId"/>.</summary>
    public string? EmojiName { get; set; }

    public string Color { get; set; } = DefaultColor;

    public int Position { get; set; }

    /// <summary>When set, only ManageChannel/ManageAnyThread holders may apply or remove this tag -
    /// a post author can't peel off a moderator's "confirmed" label.</summary>
    public bool Moderated { get; set; }

    public const string DefaultColor = "#000000";
    public const int MaxNameLength = 20;

    /// <summary>Matches Discord's cap.</summary>
    public const int MaxTagsPerForum = 20;

    public static ForumTag Create(CreateForumTagParams parameters)
    {
        var date = DateTime.UtcNow;
        var tag = new ForumTag
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = parameters.ChannelId,
            GuildId = parameters.GuildId,
            Name = parameters.Name?.Trim()!,
            EmojiId = NullIfBlank(parameters.EmojiId),
            EmojiName = NullIfBlank(parameters.EmojiName),
            Color = NullIfBlank(parameters.Color) ?? DefaultColor,
            Position = parameters.Position,
            Moderated = parameters.Moderated,
        };

        new ForumTagValidator().ValidateAndThrow(tag);

        return tag;
    }

    public class UpdateForumTagParams
    {
        public string? Name { get; init; }

        /// <summary>Empty string clears the emoji; null leaves it untouched.</summary>
        public string? EmojiId { get; init; }
        public string? EmojiName { get; init; }
        public string? Color { get; init; }
        public bool? Moderated { get; init; }
    }

    public void Update(UpdateForumTagParams parameters)
    {
        if (parameters.Name is not null) Name = parameters.Name.Trim();
        if (parameters.Color is not null) Color = parameters.Color;
        if (parameters.Moderated is not null) Moderated = parameters.Moderated.Value;

        // Setting either emoji field clears the other - a tag carries at most one emoji, and
        // making that implicit here means the caller can't produce an invalid pair by accident.
        if (parameters.EmojiId is not null)
        {
            EmojiId = NullIfBlank(parameters.EmojiId);
            if (EmojiId is not null) EmojiName = null;
        }

        if (parameters.EmojiName is not null)
        {
            EmojiName = NullIfBlank(parameters.EmojiName);
            if (EmojiName is not null) EmojiId = null;
        }

        new ForumTagValidator().ValidateAndThrow(this);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
