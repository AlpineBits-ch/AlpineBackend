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

    /// <summary>
    /// Null for a guild-scoped row, which is what an approval queue is: the character waiting on a
    /// reviewer lives in the guild's cast rather than in any one channel. Every unread group and
    /// every mention still carries one.
    /// </summary>
    public string? ChannelId { get; init; }

    public string? ChannelName { get; init; }

    /// <summary>Mirrors Guild.Domain.Enums.ChannelType, and null wherever
    /// <see cref="ChannelId"/> is.</summary>
    public int? ChannelType { get; init; }

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

    /// <summary>`Direct`, `Here`, `Everyone`, `Role` or `Persona`. Direct wins when several
    /// apply.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// For a `Persona` mention, which of the caller's characters was named, so the row can say who
    /// was pinged. The character only - who plays it is never on this wire.
    /// </summary>
    public string? PersonaId { get; init; }

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

/// <summary>What kind of thing is waiting on you.</summary>
public enum InboxTaskKind
{
    /// <summary>A chore occurrence assigned to you that is due soon or already overdue.</summary>
    ChoreDue,

    /// <summary>An open house decision you have not voted on.</summary>
    DecisionVote,

    /// <summary>An unchecked list item assigned to you.</summary>
    ListAssignment,

    /// <summary>A bill you have a share in, due or about to be.</summary>
    BillDue,

    /// <summary>A meal plan entry today or tomorrow with you down as the cook.</summary>
    CookingToday,

    /// <summary>Something in the house that is broken or overdue a service.</summary>
    MaintenanceDue,

    /// <summary>An active scene whose turn belongs to a character you answer for.</summary>
    SceneTurn,

    /// <summary>A character sitting in an approval queue you review.</summary>
    PersonaReview,

    /// <summary>One of your characters was sent back with a reason.</summary>
    PersonaChangesRequested,
}

/// <summary>One thing waiting on the caller.</summary>
public class InboxTaskDto
{
    public required InboxTaskKind Kind { get; init; }

    /// <summary>The row this task is about, in the module named by <see cref="Kind"/>: a chore
    /// occurrence, a decision, a list item, a bill occurrence, a meal plan entry or a maintenance
    /// asset. A client that does not recognise the kind still has enough to deep-link.</summary>
    public required string TargetId { get; init; }

    public required InboxBreadcrumbDto Breadcrumb { get; init; }

    public required string Title { get; init; }

    /// <summary>A short line under the title.</summary>
    public required string Subtitle { get; init; }

    /// <summary>When it is due, where that means anything.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>True when <see cref="DueAt"/> has passed, including the chore's grace period.</summary>
    public required bool IsOverdue { get; init; }
}

public class InboxTaskPageDto
{
    /// <summary>Deadlines first, soonest at the top; everything undated after them, oldest
    /// first.</summary>
    public required IReadOnlyList<InboxTaskDto> Tasks { get; init; }

    /// <summary>True when more were waiting than the cap returns.</summary>
    public required bool Truncated { get; init; }
}

/// <summary>The header badge.</summary>
public class InboxSummaryDto
{
    public required int UnreadChannelCount { get; init; }
    public required int MentionCount { get; init; }

    /// <summary>How many household items are waiting on the caller, capped the same way the others
    /// are.</summary>
    public required int TaskCount { get; init; }

    /// <summary>True when the real counts are higher than reported.</summary>
    public required bool Capped { get; init; }
}
