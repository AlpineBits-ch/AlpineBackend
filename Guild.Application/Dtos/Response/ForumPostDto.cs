using Guild.Domain.Aggregates;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>A forum post - the same Channel row a ChannelDto describes, plus the forum-specific
/// state. Hand-built for the same reason ThreadEndpoint.GetThreadsAsync builds ChannelDto by hand:
/// ChannelDto's nested facets drag in GuildDto, which nests Channels/Categories/Roles back again,
/// and materializing that graph to list posts is both wasteful and (historically) crash-prone.</summary>
public class ForumPostDto
{
    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string? ParentChannelId { get; set; }
    public ChannelType Type { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>Applied tags, ordered by the tag's own Position so the client can render them in
    /// array order without re-sorting against the tag list.</summary>
    public List<string> TagIds { get; set; } = [];

    public bool IsPinned { get; set; }
    public bool IsLocked { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? AutoArchiveAt { get; set; }
    public int? AutoArchiveMinutes { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public int MessageCount { get; set; }

    public bool IsAgeRestricted { get; set; }
    public bool IsPrivate { get; set; }
    public int SlowModeSeconds { get; set; }

    public static ForumPostDto From(Channel channel, List<string>? tagIds = null) => new()
    {
        Id = channel.Id,
        GuildId = channel.GuildId,
        ParentChannelId = channel.ParentChannelId,
        Type = channel.Type,
        Name = channel.Name,
        Description = channel.Description,
        CreatedAt = channel.CreatedAt,
        UpdatedAt = channel.UpdatedAt,
        CreatedByUserId = channel.CreatedByUserId,
        TagIds = tagIds ?? [],
        IsPinned = channel.IsPinned,
        IsLocked = channel.IsLocked,
        IsArchived = channel.IsArchived,
        AutoArchiveAt = channel.AutoArchiveAt,
        AutoArchiveMinutes = channel.AutoArchiveMinutes,
        LastActivityAt = channel.LastActivityAt,
        MessageCount = channel.MessageCount,
        IsAgeRestricted = channel.IsAgeRestricted,
        IsPrivate = channel.IsPrivate,
        SlowModeSeconds = channel.SlowModeSeconds,
    };
}

public class ForumPostPageDto
{
    public List<ForumPostDto> Posts { get; set; } = [];

    /// <summary>Opaque keyset cursor for the next page, or null at the end. Encodes the last row's
    /// full sort tuple - pinned flag, sort key, id - so a post inserted mid-scroll can't cause a
    /// skip or a duplicate the way an offset would.</summary>
    public string? NextCursor { get; set; }
}
