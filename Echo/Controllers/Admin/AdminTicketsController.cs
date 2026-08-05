using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Echo.Controllers.Admin;

/// <summary>The support inbox.</summary>
[Authorize]
[Route("api/v1/admin/tickets")]
public class AdminTicketsController(
    MicroserviceContext context,
    StaffAccess staff,
    ModerationMailer mailer)
    : AdminControllerBase(context, staff)
{
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] SupportTicketStatus? status,
        [FromQuery] SupportTicketCategory? category,
        [FromQuery] string? assignee,
        [FromQuery] bool openOnly,
        [FromQuery] int limit,
        [FromQuery] int offset,
        CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var query = Db.SupportTickets.AsNoTracking().AsQueryable();

        if (status is not null) query = query.Where(t => t.Status == status);
        if (category is not null) query = query.Where(t => t.Category == category);

        if (openOnly)
        {
            query = query.Where(t =>
                t.Status != SupportTicketStatus.Resolved && t.Status != SupportTicketStatus.Closed);
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            query = assignee switch
            {
                "me" => query.Where(t => t.AssignedToUserId == actor.UserId),
                "none" => query.Where(t => t.AssignedToUserId == null),
                _ => query.Where(t => t.AssignedToUserId == assignee),
            };
        }

        var total = await query.CountAsync(ct);
        var (take, skip) = Page(limit, offset);

        var rows = await query
            // Tickets waiting on us first; within that, the one that has waited longest.
            .OrderBy(t => t.Status == SupportTicketStatus.AwaitingRequester
                          || t.Status == SupportTicketStatus.Resolved
                          || t.Status == SupportTicketStatus.Closed)
            .ThenBy(t => t.LastActivityAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return Ok(new { total, tickets = rows.Select(t => TicketDto.From(t)) });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var ticket = await Db.SupportTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null) return NotFound();

        var messages = await Db.SupportTicketMessages.AsNoTracking()
            .Where(m => m.TicketId == ticket.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return Ok(TicketDto.From(ticket, messages));
    }

    /// <summary>Replies, or leaves an internal note.</summary>
    [HttpPost("{id}/messages")]
    public async Task<IActionResult> ReplyAsync(
        string id, [FromBody] TicketReplyRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        if (string.IsNullOrWhiteSpace(request.Body))
            return Failure(400, "body_required", "A reply needs a body.");

        var ticket = await Db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        var message = ticket.Append(
            SupportMessageAuthorKind.Staff, actor.UserId, request.Body, request.Internal, now);

        Db.SupportTicketMessages.Add(message);

        Audit(actor, ModerationAuditActions.TicketReplied, ticket.Id,
            request.Internal ? "internal note" : "reply");

        await Db.SaveChangesAsync(ct);

        if (!request.Internal)
        {
            mailer.QueueTicketReply(ticket, message.Body, token: null);
        }

        return Ok(TicketMessageDto.From(message));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateAsync(
        string id, [FromBody] UpdateTicketRequest update, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var ticket = await Db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var changes = new List<string>();

        if (update.AssignedToUserId is not null)
        {
            ticket.AssignedToUserId =
                string.IsNullOrWhiteSpace(update.AssignedToUserId) ? null : update.AssignedToUserId;

            // Deliberately does not touch LastActivityAt.
            ticket.UpdatedAt = now;
            changes.Add($"assignee={ticket.AssignedToUserId ?? "(none)"}");
        }

        if (update.Status is { } status)
        {
            ticket.SetStatus(status, now);
            changes.Add($"status={status}");
        }

        if (changes.Count > 0)
        {
            Audit(actor, ModerationAuditActions.TicketUpdated, ticket.Id, string.Join(", ", changes));
            await Db.SaveChangesAsync(ct);
        }

        return Ok(TicketDto.From(ticket));
    }
}

/// <summary>The audit log.</summary>
[Authorize]
[Route("api/v1/admin/audit")]
public class AdminAuditController(
    MicroserviceContext context,
    StaffAccess staff,
    IMessageBus bus,
    ILogger<AdminAuditController> logger)
    : AdminControllerBase(context, staff)
{
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] string? actorId,
        [FromQuery] string? subject,
        [FromQuery] string? action,
        [FromQuery] int limit,
        [FromQuery] int offset,
        CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var query = Db.ModerationAuditEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(actorId)) query = query.Where(e => e.ActorUserId == actorId);
        if (!string.IsNullOrWhiteSpace(subject)) query = query.Where(e => e.SubjectId == subject);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(e => e.Action == action);

        var total = await query.CountAsync(ct);
        var (take, skip) = Page(limit, offset);

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        // Names for every account the page mentions, resolved in one round trip rather than one per
        // row.
        var names = await ResolveNamesAsync(rows
            .SelectMany(e => new[] { e.ActorUserId, e.SubjectId })
            .Where(id => id is not null && id.StartsWith("user_", StringComparison.Ordinal))
            .Select(id => id!)
            .Distinct()
            .ToList());

        return Ok(new
        {
            total,
            entries = rows.Select(e => new
            {
                e.Id,
                e.ActorUserId,
                actorName = names.GetValueOrDefault(e.ActorUserId),
                e.Action,
                e.SubjectId,
                subjectName = e.SubjectId is null ? null : names.GetValueOrDefault(e.SubjectId),
                e.Detail,
                e.IpAddress,
                e.CreatedAt,
            }),
        });
    }

    /// <summary>Ids to display names, or an empty map.</summary>
    private async Task<Dictionary<string, string>> ResolveNamesAsync(List<string> ids)
    {
        if (ids.Count == 0) return [];

        try
        {
            var response = await bus.InvokeAsync<GetUserNamesResponse>(
                new GetUserNamesRequest { UserIds = ids });

            return response.Names;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve display names for the audit log; showing ids.");
            return [];
        }
    }
}
