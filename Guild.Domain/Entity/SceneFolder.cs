using FluentValidation;
using Guild.Domain.Validators;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateSceneFolderParams
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? ParentFolderId { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public int Position { get; set; }
}

/// <summary>
/// One shelf of a guild's scene archive, in the shape <see cref="WikiCategory"/> already uses. A
/// scene is filed under at most one; everything that has to cut across folders is a
/// <see cref="SceneTag"/>.
/// </summary>
public class SceneFolder : BaseEntity<SceneFolder>, IPrefixedEntity
{
    public static string Prefix { get; } = "scfd";

    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    public int Position { get; set; }

    /// <summary>Null for a root folder. Depth is capped at <see cref="MaxDepth"/>, which the
    /// endpoint enforces: it needs the parent's own parent, so it is a read rather than a rule.</summary>
    public string? ParentFolderId { get; set; }

    /// <summary>A single emoji shown on the rail row.</summary>
    public string? Icon { get; set; }

    public string? Color { get; set; }

    public const int MaxNameLength = 32;

    /// <summary>A third level needs a rail with its own scrollbar, which is where it stops being a rail.</summary>
    public const int MaxDepth = 2;

    public static SceneFolder Create(CreateSceneFolderParams parameters)
    {
        var date = DateTime.UtcNow;
        var folder = new SceneFolder
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = parameters.GuildId,
            Name = parameters.Name?.Trim()!,
            ParentFolderId = NullIfBlank(parameters.ParentFolderId),
            Icon = NullIfBlank(parameters.Icon),
            Color = NullIfBlank(parameters.Color),
            Position = parameters.Position,
        };

        new SceneFolderValidator().ValidateAndThrow(folder);

        return folder;
    }

    public class UpdateSceneFolderParams
    {
        public string? Name { get; init; }

        /// <summary>Empty string moves the folder to the root; null leaves it untouched.</summary>
        public string? ParentFolderId { get; init; }

        /// <summary>Empty string clears it; null leaves it untouched.</summary>
        public string? Icon { get; init; }
        public string? Color { get; init; }
        public int? Position { get; init; }
    }

    public void Update(UpdateSceneFolderParams parameters)
    {
        if (parameters.Name is not null) Name = parameters.Name.Trim();
        if (parameters.ParentFolderId is not null) ParentFolderId = NullIfBlank(parameters.ParentFolderId);
        if (parameters.Icon is not null) Icon = NullIfBlank(parameters.Icon);
        if (parameters.Color is not null) Color = NullIfBlank(parameters.Color);
        if (parameters.Position is not null) Position = parameters.Position.Value;

        new SceneFolderValidator().ValidateAndThrow(this);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
