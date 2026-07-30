using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateForumTagDto
{
    public string Name { get; set; } = null!;
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public string? Color { get; set; }
    public bool Moderated { get; set; }
}

/// <summary>Every field is null-means-unchanged.</summary>
public class UpdateForumTagDto
{
    public string? Name { get; set; }
    public string? EmojiId { get; set; }
    public string? EmojiName { get; set; }
    public string? Color { get; set; }
    public bool? Moderated { get; set; }
}

/// <summary>The complete ordered id list for the forum.</summary>
public class ReorderForumTagsDto
{
    public List<string> TagIds { get; set; } = [];
}

public class UpdateForumConfigDto
{
    public bool? RequireTag { get; set; }
    public ForumSortOrder? DefaultSortOrder { get; set; }
    public ForumLayout? DefaultLayout { get; set; }
    public string? DefaultReactionEmojiId { get; set; }
    public string? DefaultReactionEmojiName { get; set; }
    public int? DefaultThreadSlowModeSeconds { get; set; }
    public int? DefaultAutoArchiveMinutes { get; set; }
}

/// <summary>Replace semantics - the complete desired tag set, not a delta.</summary>
public class SetThreadTagsDto
{
    public List<string> TagIds { get; set; } = [];
}

public class SetThreadPinnedDto
{
    public bool Pinned { get; set; }
}

public class SetThreadLockedDto
{
    public bool Locked { get; set; }
}
