using FluentValidation;
using Guild.Domain.Validators;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateSceneTagParams
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public string? Color { get; set; }
    public int Position { get; set; }
    public bool Moderated { get; set; }
}

/// <summary>
/// A label defined once for a guild and applied to its scenes (see <see cref="SceneTagAssignment"/>).
/// The guild is the scope rather than a channel, because an arc's tags have to cross the text
/// channels its scenes were started from.
/// </summary>
public class SceneTag : BaseEntity<SceneTag>, IPrefixedEntity
{
    public static string Prefix { get; } = "sctg";

    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>A guild custom emoji id.</summary>
    public string? EmojiId { get; set; }

    /// <summary>A unicode emoji. Mutually exclusive with <see cref="EmojiId"/>.</summary>
    public string? EmojiName { get; set; }

    public string Color { get; set; } = DefaultColor;

    public int Position { get; set; }

    /// <summary>When set, only ManageScenes holders may apply or remove this tag, so a member
    /// cannot peel a "canon" label off somebody else's scene.</summary>
    public bool Moderated { get; set; }

    public const string DefaultColor = "#000000";
    public const int MaxNameLength = 20;
    public const int MaxTagsPerGuild = 40;
    public const int MaxTagsPerScene = 5;

    public static SceneTag Create(CreateSceneTagParams parameters)
    {
        var date = DateTime.UtcNow;
        var tag = new SceneTag
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = parameters.GuildId,
            Name = parameters.Name?.Trim()!,
            EmojiId = NullIfBlank(parameters.EmojiId),
            EmojiName = NullIfBlank(parameters.EmojiName),
            Color = NullIfBlank(parameters.Color) ?? DefaultColor,
            Position = parameters.Position,
            Moderated = parameters.Moderated,
        };

        new SceneTagValidator().ValidateAndThrow(tag);

        return tag;
    }

    public class UpdateSceneTagParams
    {
        public string? Name { get; init; }

        /// <summary>Empty string clears the emoji; null leaves it untouched.</summary>
        public string? EmojiId { get; init; }
        public string? EmojiName { get; init; }
        public string? Color { get; init; }
        public bool? Moderated { get; init; }
    }

    public void Update(UpdateSceneTagParams parameters)
    {
        if (parameters.Name is not null) Name = parameters.Name.Trim();
        if (parameters.Color is not null) Color = parameters.Color;
        if (parameters.Moderated is not null) Moderated = parameters.Moderated.Value;

        // Setting either emoji field clears the other, so a caller cannot produce an invalid pair.
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

        new SceneTagValidator().ValidateAndThrow(this);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
