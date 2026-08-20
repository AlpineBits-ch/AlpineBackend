namespace Guild.Application.Dtos.Request;

public class CreateSceneFolderDto
{
    public string Name { get; set; } = null!;

    /// <summary>Null or empty creates a root folder.</summary>
    public string? ParentFolderId { get; set; }

    /// <summary>A single emoji.</summary>
    public string? Icon { get; set; }

    public string? Color { get; set; }
}

public class UpdateSceneFolderDto
{
    public string? Name { get; set; }

    /// <summary>Empty string moves the folder to the root; null leaves it where it is.</summary>
    public string? ParentFolderId { get; set; }

    /// <summary>Empty string clears it; null leaves it untouched.</summary>
    public string? Icon { get; set; }

    public string? Color { get; set; }
}

public class ReorderSceneFoldersDto
{
    /// <summary>Every folder in the guild, exactly once. Position comes from the index.</summary>
    public List<string> FolderIds { get; set; } = [];
}

public class CreateSceneTagDto
{
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public bool Moderated { get; set; }
}

public class UpdateSceneTagDto
{
    public string? Name { get; set; }
    public string? Color { get; set; }

    /// <summary>Empty string clears the emoji; null leaves it untouched.</summary>
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public bool? Moderated { get; set; }
}

/// <summary>Replaces a scene's whole tag set, so removing one is the same call as adding one.</summary>
public class SetSceneTagsDto
{
    public List<string> TagIds { get; set; } = [];
}
