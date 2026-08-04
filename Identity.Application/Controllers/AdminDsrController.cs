using System.Security.Claims;
using AppEnvironment;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Controllers;

/// <summary>
/// The staff queue for data-subject rights requests that arrive out of band - by email, by post, or
/// from someone who has no account here (T1-13).
///
/// <para><b>Admin-only, checked against the database on every call.</b> The check is the same one
/// <c>MlsPolicyEndpoint</c> and Identity's <c>UserAdministrativeHandler</c> make -
/// <c>UserType == Admin</c> on the caller's own row - rather than a JWT claim or role. A token
/// outlives a demotion; the row does not, and this queue is a list of named individuals' addresses
/// and the rights they are exercising.</para>
///
/// <para><b>Every mutation writes an <c>IdentityAuditEvent</c> against the acting staff member.</b>
/// The subject of a request usually is not an account here at all, so the audit row's <c>UserId</c> is
/// deliberately the staff member, not the subject - "who closed this, and when" is the question a
/// regulator asks, and it has to be answerable about a person.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/admin/dsr")]
public class AdminDsrController(
    MicroserviceContext ctx,
    ILogger<AdminDsrController> logger) : ControllerBase
{
    /// <summary>
    /// Opens a request. The statutory deadline is stamped here, once, from the receipt time, and is
    /// never recalculated afterwards.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<DataSubjectRequestDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> OpenAsync([FromBody] OpenDataSubjectRequest request, CancellationToken ct)
    {
        var staffUserId = await RequireAdminAsync(ct);
        if (staffUserId is null) return AdminForbidden();

        if (string.IsNullOrWhiteSpace(request.SubjectEmail) || !request.SubjectEmail.Contains('@'))
            return BadRequest("subjectEmail is required and must be an email address.");

        if (!Enum.IsDefined(request.Type))
            return BadRequest($"type must be one of: {string.Join(", ", Enum.GetNames<DataSubjectRequestType>())}.");

        var now = DateTimeOffset.UtcNow;
        if (request.ReceivedAt > now)
            return BadRequest("receivedAt cannot be in the future.");

        var entity = DataSubjectRequest.Create(new CreateDataSubjectRequestParams
        {
            SubjectEmail = request.SubjectEmail,
            Type = request.Type,
            Notes = request.Notes,
            OpenedByStaffUserId = staffUserId,
            ReceivedAt = request.ReceivedAt ?? now,
            ResponseWindow = Env.Privacy.DataSubjectRequestResponseWindow,
        });

        // Best-effort match to an account, purely so staff do not have to look it up by hand. Never
        // a requirement: a request from an address with no account is exactly the case this queue
        // exists for, and failing to match one is not an error.
        entity.SubjectUserId = await ctx.Users
            .Where(u => u.NormalizedEmail == entity.SubjectEmail.ToUpperInvariant())
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);

        ctx.DataSubjectRequests.Add(entity);
        Audit(staffUserId, IdentityAuditActions.DsrOpened,
            $"{entity.Id} {entity.Type}, due {entity.DueAt:u}");

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("DSR {Id} ({Type}) opened by {Staff}, due {DueAt}",
            entity.Id, entity.Type, staffUserId, entity.DueAt);

        return Created($"/api/v1/admin/dsr/{entity.Id}", DataSubjectRequestDto.From(entity, now));
    }

    /// <summary>
    /// The queue.
    /// </summary>
    /// <param name="status">Filter to one status. Omit for everything.</param>
    /// <param name="overdueOnly">Only requests past their deadline and still unanswered.</param>
    /// <param name="limit">Page size, capped at 200.</param>
    /// <param name="ct">Request cancellation.</param>
    [HttpGet]
    [ProducesResponseType<object>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(
        [FromQuery] DataSubjectRequestStatus? status,
        [FromQuery] bool overdueOnly,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var staffUserId = await RequireAdminAsync(ct);
        if (staffUserId is null) return AdminForbidden();

        var now = DateTimeOffset.UtcNow;
        var query = ctx.DataSubjectRequests.AsNoTracking().AsQueryable();

        if (status is not null) query = query.Where(r => r.Status == status);
        if (overdueOnly)
        {
            query = query.Where(r => r.Status != DataSubjectRequestStatus.Closed && r.DueAt < now);
        }

        var page = Math.Clamp(limit == 0 ? 50 : limit, 1, 200);

        // Open work first, then soonest deadline. A closed request sorts last regardless of its
        // deadline - the queue is a worklist, and the thing that must never be buried is the request
        // that is about to breach.
        var rows = await query
            .OrderBy(r => r.Status == DataSubjectRequestStatus.Closed)
            .ThenBy(r => r.DueAt)
            .Take(page)
            .ToListAsync(ct);

        // Counted over the whole table, not the page: an overdue banner computed from one page of
        // results is exactly as wrong as no banner on the day it matters.
        var overdue = await ctx.DataSubjectRequests
            .CountAsync(r => r.Status != DataSubjectRequestStatus.Closed && r.DueAt < now, ct);

        return Ok(new
        {
            overdueCount = overdue,
            responseWindowDays = (int)Env.Privacy.DataSubjectRequestResponseWindow.TotalDays,
            requests = rows.Select(r => DataSubjectRequestDto.From(r, now)),
        });
    }

    /// <summary>Progresses or closes a request.</summary>
    [HttpPatch("{id}")]
    [ProducesResponseType<DataSubjectRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        string id, [FromBody] UpdateDataSubjectRequest update, CancellationToken ct)
    {
        var staffUserId = await RequireAdminAsync(ct);
        if (staffUserId is null) return AdminForbidden();

        var entity = await ctx.DataSubjectRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        if (update.Status is not null && !Enum.IsDefined(update.Status.Value))
            return BadRequest("status is not a known value.");

        if (update.Disposition is not null && !Enum.IsDefined(update.Disposition.Value))
            return BadRequest("disposition is not a known value.");

        var closing = update.Status == DataSubjectRequestStatus.Closed;

        // Closing without a disposition is refused rather than defaulted. "Closed, outcome unknown"
        // is indistinguishable from "we stopped looking at it", and the record has to be able to tell
        // a regulator which of those happened.
        if (closing && update.Disposition is null or DataSubjectRequestDisposition.None)
        {
            return BadRequest(
                "Closing a request requires a disposition other than None: "
                + string.Join(", ", Enum.GetNames<DataSubjectRequestDisposition>()
                    .Where(n => n != nameof(DataSubjectRequestDisposition.None))) + ".");
        }

        if (!closing && update.Disposition is not null and not DataSubjectRequestDisposition.None)
            return BadRequest("A disposition can only be set when closing the request.");

        if (update.AssignedToStaffUserId is not null)
        {
            entity.AssignedToStaffUserId =
                string.IsNullOrWhiteSpace(update.AssignedToStaffUserId) ? null : update.AssignedToStaffUserId;
        }

        if (update.SubjectUserId is not null)
        {
            entity.SubjectUserId =
                string.IsNullOrWhiteSpace(update.SubjectUserId) ? null : update.SubjectUserId;
        }

        entity.AppendNote(update.Note ?? string.Empty, staffUserId, now);

        if (closing)
        {
            entity.Close(update.Disposition!.Value, staffUserId, now);
            Audit(staffUserId, IdentityAuditActions.DsrClosed,
                $"{entity.Id} {entity.Type} -> {entity.Disposition}"
                + (entity.ClosedAt > entity.DueAt ? " (LATE)" : string.Empty));
        }
        else
        {
            if (update.Status is not null)
            {
                if (entity.Status == DataSubjectRequestStatus.Closed)
                    entity.Reopen(update.Status.Value, now);
                else
                    entity.Status = update.Status.Value;
            }

            entity.UpdatedAt = now;
            Audit(staffUserId, IdentityAuditActions.DsrUpdated, $"{entity.Id} -> {entity.Status}");
        }

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("DSR {Id} updated by {Staff}: status {Status}, disposition {Disposition}",
            entity.Id, staffUserId, entity.Status, entity.Disposition);

        return Ok(DataSubjectRequestDto.From(entity, now));
    }

    /// <summary>
    /// A 403 with a machine-readable code, rather than <c>Forbid()</c>.
    ///
    /// <para>Explicit because the refusal has to be distinguishable from an expired token (401) by a
    /// staff console, and because the challenge-based <c>Forbid()</c> delegates the status to whatever
    /// the configured authentication handler decides - which is not a decision this route should be
    /// delegating.</para>
    /// </summary>
    private IActionResult AdminForbidden() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "admin_required",
            message = "This endpoint requires a platform administrator account.",
        });

    /// <summary>The caller's id when they are a platform administrator, otherwise null.</summary>
    private async Task<string?> RequireAdminAsync(CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return null;

        var isAdmin = await ctx.Users.AnyAsync(u => u.Id == userId && u.UserType == UserType.Admin, ct);
        if (!isAdmin)
        {
            logger.LogWarning("Non-administrative account {UserId} attempted to reach the DSR queue", userId);
            return null;
        }

        return userId;
    }

    private void Audit(string staffUserId, string action, string detail)
    {
        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = staffUserId,
            Action = action,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }));
    }
}
