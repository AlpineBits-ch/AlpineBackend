namespace Guild.Application.Dtos.Response;

/// <summary>
/// Where an unread channel sits, for the `GUILD › CATEGORY › CHANNEL` line under each group.
/// </summary>
public class InboxBreadcrumbDto
{
    public required string GuildId { get; init; }
    public required string GuildName { get; init; }

    /// <summary>Public, anonymous icon route for this guild.</summary>
    public required string GuildIconUrl { get; init; }

    /// <summary>Thumbnail variant of the same, for the small avatar the inbox actually renders.</summary>
    public required string GuildIconThumbnailUrl { get; init; }

    public string? CategoryId { get; init; }
    public string? CategoryName { get; init; }

    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }

    /// <summary>Mirrors Guild.Domain.Enums.ChannelType.</summary>
    public required int ChannelType { get; init; }

    /// <summary>Set for threads and forum posts, so the client can show the channel they live
    /// under.</summary>
    public string? ParentChannelId { get; init; }
    public string? ParentChannelName { get; init; }
}

/// <summary>One preview line under an unread group. Mirrors Messaging's InboxMessagePreview.</summary>
public class InboxMessageDto
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string AuthorId { get; init; }
    public string? AuthorDisplayName { get; init; }
    public string? AuthorAvatarUrl { get; init; }

    /// <summary>Ciphertext when <see cref="IsEncrypted"/>.</summary>
    public byte[] Content { get; init; } = [];

    public bool IsEncrypted { get; init; }
    public int? MlsGeneration { get; init; }
    public int Type { get; init; }
    public int? SystemMessageVariant { get; init; }
    public string? EmbedsJson { get; init; }
}

/// <summary>One channel's worth of unread, which is what the UI renders as a group.</summary>
public class InboxUnreadGroupDto
{
    public required InboxBreadcrumbDto Breadcrumb { get; init; }

    /// <summary>Timestamp of the newest message in the channel.</summary>
    public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>Best-effort, and clamped at zero.</summary>
    public required int UnreadCount { get; init; }

    public required int MentionCount { get; init; }

    /// <summary>Oldest-first, capped.</summary>
    public required IReadOnlyList<InboxMessageDto> Previews { get; init; }

    /// <summary>True when the channel has more unread than the preview cap.</summary>
    public required bool PreviewsTruncated { get; init; }
}

public class InboxUnreadPageDto
{
    public required IReadOnlyList<InboxUnreadGroupDto> Groups { get; init; }

    /// <summary>Opaque; pass back as `cursor` for the next page. Null when this is the last one.</summary>
    public string? NextCursor { get; init; }

    /// <summary>True when Messaging could not be reached for previews.</summary>
    public bool PreviewsUnavailable { get; init; }
}

/// <summary>One row of the Mentions tab.</summary>
public class InboxMentionDto
{
    public required string MessageId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>`Direct`, `Here`, `Everyone` or `Role`. Direct wins when several apply.</summary>
    public required string Kind { get; init; }

    /// <summary>Set for role pings, so the client can show which role was named.</summary>
    public string? RoleId { get; init; }
    public string? RoleName { get; init; }

    public required string AuthorId { get; init; }

    /// <summary>Null for a DM mention.</summary>
    public InboxBreadcrumbDto? Breadcrumb { get; init; }

    public string? ConversationId { get; init; }

    /// <summary>The message itself.</summary>
    public InboxMessageDto? Message { get; init; }
}

public class InboxMentionPageDto
{
    public required IReadOnlyList<InboxMentionDto> Mentions { get; init; }

    /// <summary>Opaque; pass back as `cursor`. Null on the last page.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>The header badge.</summary>
public class InboxSummaryDto
{
    public required int UnreadChannelCount { get; init; }
    public required int MentionCount { get; init; }

    /// <summary>True when the real counts are higher than reported.</summary>
    public required bool Capped { get; init; }
}
