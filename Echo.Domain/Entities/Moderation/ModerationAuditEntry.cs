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

    // The status page.
    public const string StatusIncidentCreated = "status.incident-created";
    public const string StatusIncidentUpdated = "status.incident-updated";
    public const string StatusIncidentEdited = "status.incident-edited";
    public const string StatusIncidentConfirmed = "status.incident-confirmed";
    public const string StatusIncidentRetracted = "status.incident-retracted";
    public const string StatusComponentCreated = "status.component-created";
    public const string StatusComponentUpdated = "status.component-updated";

    // Billing.
    public const string BillingGrantIssued = "billing.grant-issued";
    public const string BillingGrantRevoked = "billing.grant-revoked";
    public const string BillingGrantAmended = "billing.grant-amended";
    public const string BillingPlanCreated = "billing.plan-created";
    public const string BillingPlanEdited = "billing.plan-edited";
    public const string BillingPlanVersionActivated = "billing.plan-version-activated";
    public const string BillingPlanVersionArchived = "billing.plan-version-archived";
    public const string BillingPlanArchived = "billing.plan-archived";
    public const string BillingPlanAssigned = "billing.plan-assigned";
    public const string BillingCacheInvalidated = "billing.cache-invalidated";

    /// <summary>An account's staff tier changed.</summary>
    public const string RoleChanged = "user.role-changed";
}
