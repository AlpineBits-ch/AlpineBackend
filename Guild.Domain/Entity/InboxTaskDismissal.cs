using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// One Waiting-on-you row the caller has put away. The tab is derived state - a scene turn, an
/// approval queue and a chore occurrence are all computed from the module that owns them - so
/// without a row like this there is nothing for a dismissal to write to and the X cannot stick.
/// </summary>
public class InboxTaskDismissal : BaseEntity<InboxTaskDismissal>, IPrefixedEntity
{
    public static string Prefix { get; } = "itdm";

    public string UserId { get; set; } = null!;

    /// <summary>The kind's name rather than its ordinal, because the enum lives in the DTO layer
    /// and renumbering it must not reinterpret stored rows.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Part of the key: a character can be submitted in two guilds at once, and putting
    /// one queue away must not empty the other.</summary>
    public string GuildId { get; set; } = null!;

    /// <summary>Whatever the kind's <c>targetId</c> is - a scene channel, a persona, an occurrence.</summary>
    public string TargetId { get; set; } = null!;

    /// <summary>
    /// The instant the row was put away, and the whole of the re-arming rule: the task comes back
    /// as soon as its own stamp moves past this, so the next turn of the same scene and a
    /// resubmitted character are new rows rather than ones already dismissed.
    /// </summary>
    public DateTimeOffset DismissedAt { get; set; }
}
