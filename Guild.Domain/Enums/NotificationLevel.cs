namespace Guild.Domain.Enums;

/// <summary>How much a member wants to hear from a guild, category or channel.</summary>
public enum NotificationLevel
{
    /// <summary>Notify on every message. Discord's default for a newly joined guild, and ours.</summary>
    AllMessages,

    /// <summary>Notify only when the member is mentioned directly, by a role they hold, or by
    /// @everyone/@here (unless those are separately suppressed).</summary>
    OnlyMentions,

    /// <summary>Never notify.</summary>
    Nothing,
}
