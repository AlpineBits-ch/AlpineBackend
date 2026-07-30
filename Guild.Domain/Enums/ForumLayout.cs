namespace Guild.Domain.Enums;

/// <summary>Presentation hint for a forum's post list. Stored and echoed so the choice syncs
/// across a user's devices - the backend never acts on it. Mirrors Discord's
/// <c>default_forum_layout</c>.</summary>
public enum ForumLayout
{
    List,
    Gallery,
}
