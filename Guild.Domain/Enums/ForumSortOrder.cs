namespace Guild.Domain.Enums;

/// <summary>Default ordering a forum's post list is served in when the caller doesn't ask for a
/// specific one. Mirrors Discord's <c>default_sort_order</c>.</summary>
public enum ForumSortOrder
{
    /// <summary>Most recently posted-in first, falling back to creation time for posts that have
    /// no messages yet.</summary>
    LatestActivity,

    /// <summary>Newest post first, ignoring subsequent activity.</summary>
    CreationDate,
}
