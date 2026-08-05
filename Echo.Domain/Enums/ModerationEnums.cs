namespace Echo.Domain.Enums;

/// <summary>What a report is about. Decides whether <c>SubjectId</c> means anything.</summary>
public enum ReportSubjectKind
{
    /// <summary>The account itself - its name, avatar, bio, or a pattern of behaviour.</summary>
    User,

    Message,
    Channel,
    Guild,
}

/// <summary>
/// Why something was reported, and - reused on <see cref="ModerationActionKind"/> rows - why staff
/// acted.
/// </summary>
public enum ReportReason
{
    Other,
    Spam,
    Harassment,
    HateSpeech,
    ViolentThreats,
    SelfHarm,
    SexualContent,

    /// <summary>Anything involving a minor. Always <see cref="ReportPriority.Critical"/>.</summary>
    ChildSafety,

    Impersonation,
    Malware,
    IllegalContent,
}

/// <summary>How fast a report has to be looked at.</summary>
public enum ReportPriority
{
    Low,
    Normal,
    High,

    /// <summary>Physical-safety categories. Sorted to the top of the queue regardless of age.</summary>
    Critical,
}

public enum ReportStatus
{
    Open,

    /// <summary>Picked up by a moderator.</summary>
    Triaged,

    ActionTaken,
    Dismissed,
    Duplicate,
}

public enum ModerationActionKind
{
    /// <summary>Recorded against the account, invisible to it.</summary>
    Note,

    Warning,

    /// <summary>Time-limited. <c>ExpiresAt</c> is always set.</summary>
    Suspension,

    /// <summary>Indefinite unless <c>ExpiresAt</c> says otherwise.</summary>
    Ban,

    Unban,
}

public enum AppealStatus
{
    Pending,
    UnderReview,
    Granted,
    Denied,
}

public enum SupportTicketCategory
{
    Other,
    Account,
    Billing,
    Technical,

    /// <summary>Reports of harm that arrive through support rather than through the client's own
    /// report flow - which is how they arrive when the reporter has no account, or has been banned.</summary>
    Safety,

    Privacy,
}

public enum SupportTicketStatus
{
    Open,

    /// <summary>Staff answered; the ball is with the requester.</summary>
    AwaitingRequester,

    AwaitingStaff,
    Resolved,
    Closed,
}

public enum SupportMessageAuthorKind
{
    Requester,
    Staff,

    /// <summary>Written by the platform, not a person - "this ticket was opened from an appeal",
    /// "status changed to Resolved".</summary>
    System,
}

/// <summary>
/// The staff tier of a caller, resolved from Identity's <c>UserType</c> on every request.
/// </summary>
public enum StaffRole
{
    None,
    Moderator,
    Admin,
}
