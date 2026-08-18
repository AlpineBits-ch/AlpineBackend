namespace Guild.Contracts.Bus.Events;

/// <summary>
/// "These users should get a phone notification about a scene's stale turn." Published by Guild,
/// which owns scenes and the notification settings, and consumed by Messaging, which owns Firebase.
/// </summary>
public class SceneTurnPushRequested
{
    public string GuildId { get; set; } = null!;

    /// <summary>The scene channel, so tapping the notification opens the scene.</summary>
    public string ChannelId { get; set; } = null!;

    /// <summary>Who to notify.</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>The character whose turn it is.</summary>
    public string PersonaId { get; set; } = null!;

    /// <summary>
    /// The character's name in this guild, which is what the notification renders under. Never the
    /// account name: a lock screen naming the player says who plays whom to whoever picks the phone
    /// up. Empty when it could not be resolved, which is what <see cref="PersonaHidden"/> marks.
    /// </summary>
    public string AuthorDisplayName { get; set; } = "";

    /// <summary>Set when the character has no name to send, so a client masks it rather than
    /// guessing that the notification is about nobody in particular.</summary>
    public bool PersonaHidden { get; set; }

    /// <summary>The scene's title.</summary>
    public string SceneName { get; set; } = "";

    public DateTimeOffset? TurnDeadlineAt { get; set; }

    /// <summary>How many times this turn has been chased.</summary>
    public int NudgeCount { get; set; }

    /// <summary>
    /// True on the copy addressed to whoever holds ManageScenes after a second miss, which reads as
    /// "this character has gone quiet" rather than "it is your turn".
    /// </summary>
    public bool Escalated { get; set; }
}
