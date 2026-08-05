using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;

namespace Echo.Moderation;

// ── Requests ────────────────────────────────────────────────────────────────

/// <summary>Filed by a signed-in user from the client's report flow.</summary>
public class SubmitReportRequest
{
    public string TargetUserId { get; set; } = string.Empty;
    public ReportSubjectKind SubjectKind { get; set; }
    public string? SubjectId { get; set; }
    public ReportReason Reason { get; set; }
    public string? Details { get; set; }

    /// <summary>The reporting client's own snapshot of what it saw, as JSON.</summary>
    public object? Evidence { get; set; }
}

public class UpdateReportRequest
{
    public ReportStatus? Status { get; set; }

    /// <summary>Empty string un-assigns; null leaves the assignee alone.</summary>
    public string? AssignedToUserId { get; set; }

    public string? Resolution { get; set; }
    public string? DuplicateOfId { get; set; }
}

public class IssueActionRequest
{
    public ModerationActionKind Kind { get; set; }
    public ReportReason Reason { get; set; }
    public string? PublicNote { get; set; }
    public string? InternalNote { get; set; }

    /// <summary>Required for a suspension, refused for anything else.</summary>
    public int? DurationHours { get; set; }

    public string? ReportId { get; set; }

    /// <summary>Whether to email the user. Defaults to true; a Note never mails regardless.</summary>
    public bool Notify { get; set; } = true;
}

/// <summary>Promotes or demotes a staff account. Administrators only.</summary>
public class SetRoleRequest
{
    /// <summary><c>Default</c>, <c>Moderator</c> or <c>Admin</c>.</summary>
    public string Role { get; set; } = "Default";
}

public class RevokeActionRequest
{
    public string? Reason { get; set; }
    public bool Notify { get; set; } = true;
}

public class DecideAppealRequest
{
    public bool Granted { get; set; }
    public string Note { get; set; } = string.Empty;
    public bool Notify { get; set; } = true;
}

public class UpdateTicketRequest
{
    public SupportTicketStatus? Status { get; set; }
    public string? AssignedToUserId { get; set; }
}

public class TicketReplyRequest
{
    public string Body { get; set; } = string.Empty;

    /// <summary>A staff-only note. Never reaches the requester and never mails them.</summary>
    public bool Internal { get; set; }
}

public class OpenTicketRequest
{
    public string Email { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public SupportTicketCategory Category { get; set; }
    public string Body { get; set; } = string.Empty;
}

public class PublicReplyRequest
{
    public string Body { get; set; } = string.Empty;
}

public class SubmitAppealRequest
{
    /// <summary>The action's public reference, as printed in the ban notice.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

// ── Responses ───────────────────────────────────────────────────────────────

public class ReportDto
{
    public string Id { get; set; } = string.Empty;
    public string? ReporterUserId { get; set; }
    public string TargetUserId { get; set; } = string.Empty;
    public string SubjectKind { get; set; } = string.Empty;
    public string? SubjectId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string? AssignedToUserId { get; set; }
    public string? Resolution { get; set; }
    public string? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? DuplicateOfId { get; set; }
    public int DuplicateCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Only populated on the single-report read. Raw JSON, passed through as-is.</summary>
    public string? Evidence { get; set; }

    /// <summary>True whenever evidence is present, because nothing corroborates it today.</summary>
    public bool EvidenceIsUnverified { get; set; }

    public static ReportDto From(ModerationReport r, bool includeEvidence = false) => new()
    {
        Id = r.Id,
        ReporterUserId = r.ReporterUserId,
        TargetUserId = r.TargetUserId,
        SubjectKind = r.SubjectKind.ToString(),
        SubjectId = r.SubjectId,
        Reason = r.Reason.ToString(),
        Details = r.Details,
        Status = r.Status.ToString(),
        Priority = r.Priority.ToString(),
        AssignedToUserId = r.AssignedToUserId,
        Resolution = r.Resolution,
        ResolvedByUserId = r.ResolvedByUserId,
        ResolvedAt = r.ResolvedAt,
        DuplicateOfId = r.DuplicateOfId,
        DuplicateCount = r.DuplicateCount,
        CreatedAt = r.CreatedAt,
        Evidence = includeEvidence ? r.EvidenceJson : null,
        EvidenceIsUnverified = r.EvidenceJson is not null,
    };
}

public class ActionDto
{
    public string Id { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string PublicNote { get; set; } = string.Empty;
    public string? InternalNote { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedByUserId { get; set; }
    public string? RevocationReason { get; set; }
    public string? ReportId { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Built for staff.</summary>
    public static ActionDto From(ModerationAction a, DateTimeOffset now, bool includeInternal = true) => new()
    {
        Id = a.Id,
        Reference = a.Reference,
        TargetUserId = a.TargetUserId,
        ActorUserId = a.ActorUserId,
        Kind = a.Kind.ToString(),
        Reason = a.Reason.ToString(),
        PublicNote = a.PublicNote,
        InternalNote = includeInternal ? a.InternalNote : null,
        ExpiresAt = a.ExpiresAt,
        RevokedAt = a.RevokedAt,
        RevokedByUserId = a.RevokedByUserId,
        RevocationReason = a.RevocationReason,
        ReportId = a.ReportId,
        Active = a.IsActiveAt(now),
        CreatedAt = a.CreatedAt,
    };
}

public class AppealDto
{
    public string Id { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string? ActionReference { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
    public string? SubmittedByUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ActionDto? Action { get; set; }

    public static AppealDto From(ModerationAppeal a, DateTimeOffset now) => new()
    {
        Id = a.Id,
        Reference = a.Reference,
        ActionId = a.ActionId,
        ActionReference = a.Action?.Reference,
        ContactEmail = a.ContactEmail,
        SubmittedByUserId = a.SubmittedByUserId,
        Body = a.Body,
        Status = a.Status.ToString(),
        DecidedByUserId = a.DecidedByUserId,
        DecidedAt = a.DecidedAt,
        DecisionNote = a.DecisionNote,
        CreatedAt = a.CreatedAt,
        Action = a.Action is null ? null : ActionDto.From(a.Action, now),
    };
}

public class TicketMessageDto
{
    public string Id { get; set; } = string.Empty;
    public string AuthorKind { get; set; } = string.Empty;
    public string? AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool Internal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static TicketMessageDto From(SupportTicketMessage m) => new()
    {
        Id = m.Id,
        AuthorKind = m.AuthorKind.ToString(),
        AuthorUserId = m.AuthorUserId,
        Body = m.Body,
        Internal = m.IsInternal,
        CreatedAt = m.CreatedAt,
    };
}

public class TicketDto
{
    public string Id { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string? RequesterUserId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? AssignedToUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public List<TicketMessageDto> Messages { get; set; } = [];

    public static TicketDto From(SupportTicket t, IEnumerable<SupportTicketMessage>? messages = null) => new()
    {
        Id = t.Id,
        Reference = t.Reference,
        ContactEmail = t.ContactEmail,
        RequesterUserId = t.RequesterUserId,
        Subject = t.Subject,
        Category = t.Category.ToString(),
        Status = t.Status.ToString(),
        AssignedToUserId = t.AssignedToUserId,
        CreatedAt = t.CreatedAt,
        LastActivityAt = t.LastActivityAt,
        Messages = (messages ?? []).Select(TicketMessageDto.From).ToList(),
    };
}

/// <summary>The public view of a ticket.</summary>
public class PublicTicketDto
{
    public string Reference { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public List<PublicTicketMessageDto> Messages { get; set; } = [];
}

public class PublicTicketMessageDto
{
    /// <summary>"You" or "Support".</summary>
    public string From { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public class StaffSessionDto
{
    public string UserId { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool CanViewAudit { get; set; }
}

public class ModerationStatsDto
{
    public int OpenReports { get; set; }
    public int CriticalReports { get; set; }
    public int HighReports { get; set; }
    public int UnassignedReports { get; set; }

    /// <summary>Open reports older than 48 hours. The one number that says the queue is losing.</summary>
    public int StaleReports { get; set; }

    public int OpenAppeals { get; set; }
    public int OpenTickets { get; set; }
    public int TicketsAwaitingStaff { get; set; }
    public int ActiveBans { get; set; }
    public int ActiveSuspensions { get; set; }
}
