using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

public class SceneFolderDto
{
    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Position { get; set; }
    public string? ParentFolderId { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }

    public static SceneFolderDto From(SceneFolder folder) => new()
    {
        Id = folder.Id,
        GuildId = folder.GuildId,
        Name = folder.Name,
        Position = folder.Position,
        ParentFolderId = folder.ParentFolderId,
        Icon = folder.Icon,
        Color = folder.Color,
    };
}

public class SceneTagDto
{
    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;

    /// <summary>#000000 is the "no colour chosen" default, not real black.</summary>
    public string Color { get; set; } = null!;

    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public int Position { get; set; }
    public bool Moderated { get; set; }

    public static SceneTagDto From(SceneTag tag) => new()
    {
        Id = tag.Id,
        GuildId = tag.GuildId,
        Name = tag.Name,
        Color = tag.Color,
        EmojiId = tag.EmojiId,
        EmojiName = tag.EmojiName,
        Position = tag.Position,
        Moderated = tag.Moderated,
    };
}

/// <summary>
/// A guild's whole archive vocabulary. Read and broadcast as one set: both halves are small and
/// bounded, and replacing the lot has no rename, reorder or delete edge case to get wrong.
/// </summary>
public class SceneTaxonomyDto
{
    public string GuildId { get; set; } = null!;
    public List<SceneFolderDto> Folders { get; set; } = [];
    public List<SceneTagDto> Tags { get; set; } = [];
}
