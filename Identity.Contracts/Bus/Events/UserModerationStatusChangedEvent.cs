namespace Identity.Contracts.Bus.Events;

/// <summary>An account was banned or restored by staff.</summary>
public class UserModerationStatusChangedEvent
{
    /// <summary>The account that was actioned, never the staff member.</summary>
    public string UserId { get; set; } = null!;

    /// <summary>Whether the account is now barred from signing in.</summary>
    public bool Banned { get; set; }

    /// <summary>The account's <c>UserStatus</c> after the change, as its enum name.</summary>
    public string Status { get; set; } = null!;

    /// <summary>The status it moved away from.</summary>
    public string PreviousStatus { get; set; } = null!;

    /// <summary>The staff account that made the change.</summary>
    public string ActorUserId { get; set; } = null!;

    /// <summary>Free text from the console, already prefixed with the moderation action kind. Empty
    /// when the caller supplied none, which is possible on the unban path.</summary>
    public string? Reason { get; set; }

    /// <summary>When the row moved, from the producer's clock.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
