using Persistence;

namespace Echo.Domain.Entities.Moderation;

/// <summary>Every staff mutation in the console, with the actor on it.</summary>
public class ModerationAuditEntry : BaseEntity<ModerationAuditEntry>, IPrefixedEntity
{
    public static string Prefix => "maud";

    public string ActorUserId { get; set; } = null!;

    /// <summary>Dotted verb, e.g. <c>report.resolved</c>. See <see cref="ModerationAuditActions"/>.</summary>
    public string Action { get; set; } = null!;

    /// <summary>What was acted on - a report id, an action id, a target user id.</summary>
    public string? SubjectId { get; set; }

    public string? Detail { get; set; }

    public string? IpAddress { get; set; }

    public static ModerationAuditEntry Create(
        string actorUserId, string action, string? subjectId, string? detail, string? ipAddress,
        DateTimeOffset now) =>
        new()
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            ActorUserId = actorUserId,
            Action = action,
            SubjectId = subjectId,
            Detail = detail?.Length > 1000 ? detail[..1000] : detail,
            IpAddress = ipAddress,
        };
}

public static class ModerationAuditActions
{
    public const string ReportAssigned = "report.assigned";
    public const string ReportResolved = "report.resolved";
    public const string ReportReopened = "report.reopened";

    public const string ActionIssued = "action.issued";
    public const string ActionRevoked = "action.revoked";

    public const string AppealClaimed = "appeal.claimed";
    public const string AppealDecided = "appeal.decided";

    public const string TicketReplied = "ticket.replied";
    public const string TicketUpdated = "ticket.updated";

    public const string UserViewed = "user.viewed";

    /// <summary>An account's staff tier changed.</summary>
    public const string RoleChanged = "user.role-changed";
}
