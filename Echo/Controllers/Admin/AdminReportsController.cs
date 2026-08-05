using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Echo.Controllers.Admin;

/// <summary>The report queue.</summary>
[Authorize]
[Route("api/v1/admin/reports")]
public class AdminReportsController(MicroserviceContext context, StaffAccess staff)
    : AdminControllerBase(context, staff)
{
    /// <summary>The queue.</summary>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] ReportStatus? status,
        [FromQuery] ReportReason? reason,
        [FromQuery] ReportPriority? priority,
        [FromQuery] string? assignee,
        [FromQuery] bool openOnly,
        [FromQuery] int limit,
        [FromQuery] int offset,
        CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var query = Db.ModerationReports.AsNoTracking().AsQueryable();

        if (status is not null) query = query.Where(r => r.Status == status);
        if (reason is not null) query = query.Where(r => r.Reason == reason);
        if (priority is not null) query = query.Where(r => r.Priority == priority);

        if (openOnly)
        {
            query = query.Where(r => r.Status == ReportStatus.Open || r.Status == ReportStatus.Triaged);
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            // "me" resolves server-side so the console never has to know its own user id to filter
            // its own queue, and "none" is the unclaimed pile - the two views a moderator actually
            // switches between.
            query = assignee switch
            {
                "me" => query.Where(r => r.AssignedToUserId == actor.UserId),
                "none" => query.Where(r => r.AssignedToUserId == null),
                _ => query.Where(r => r.AssignedToUserId == assignee),
            };
        }

        var total = await query.CountAsync(ct);
        var (take, skip) = Page(limit, offset);

        var rows = await query
            .OrderBy(r => r.Status != ReportStatus.Open && r.Status != ReportStatus.Triaged)
            .ThenByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return Ok(new
        {
            total,
            reports = rows.Select(r => ReportDto.From(r)),
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var report = await Db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null) return NotFound();

        // Everything ever done to the reported account, so a moderator sees the pattern rather than
        // this one report in isolation - which is the difference between a first-offence warning and
        // a fourth-strike ban.
        var now = DateTimeOffset.UtcNow;
        var history = await Db.ModerationActions.AsNoTracking()
            .Where(a => a.TargetUserId == report.TargetUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        var otherReports = await Db.ModerationReports.AsNoTracking()
            .Where(r => r.TargetUserId == report.TargetUserId && r.Id != report.Id)
            .CountAsync(ct);

        return Ok(new
        {
            report = ReportDto.From(report, includeEvidence: true),
            targetReportCount = otherReports,
            history = history.Select(a => ActionDto.From(a, now)),
        });
    }

    /// <summary>Triage: assign, re-status, resolve, mark duplicate.</summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateAsync(
        string id, [FromBody] UpdateReportRequest update, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var report = await Db.ModerationReports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        if (update.AssignedToUserId is not null)
        {
            // Empty string releases the report; null (absent) leaves it alone.
            report.Assign(string.IsNullOrWhiteSpace(update.AssignedToUserId) ? null : update.AssignedToUserId, now);
            Audit(actor, ModerationAuditActions.ReportAssigned, report.Id,
                report.AssignedToUserId is null ? "released" : $"assigned to {report.AssignedToUserId}");
        }

        if (update.Status is { } status)
        {
            if (status is ReportStatus.ActionTaken or ReportStatus.Dismissed or ReportStatus.Duplicate)
            {
                // Closing without a reason is refused rather than defaulted.
                if (string.IsNullOrWhiteSpace(update.Resolution))
                    return Failure(400, "resolution_required", "Closing a report requires a resolution note.");

                if (status == ReportStatus.Duplicate)
                {
                    if (string.IsNullOrWhiteSpace(update.DuplicateOfId))
                        return Failure(400, "duplicate_of_required", "Marking a report duplicate requires the original's id.");

                    if (update.DuplicateOfId == report.Id)
                        return Failure(400, "duplicate_of_self", "A report cannot be a duplicate of itself.");

                    var original = await Db.ModerationReports
                        .FirstOrDefaultAsync(r => r.Id == update.DuplicateOfId, ct);

                    if (original is null)
                        return Failure(400, "duplicate_of_missing", "No report with that id.");

                    report.DuplicateOfId = original.Id;

                    // The original carries the weight of both.
                    original.DuplicateCount++;
                    original.UpdatedAt = now;
                }

                report.Resolve(status, update.Resolution!, actor.UserId, now);
                Audit(actor, ModerationAuditActions.ReportResolved, report.Id, $"closed as {status}");
            }
            else
            {
                if (!report.IsOpen)
                {
                    report.Reopen(now);
                    Audit(actor, ModerationAuditActions.ReportReopened, report.Id);
                }

                report.Status = status;
                report.UpdatedAt = now;
            }
        }
        else if (update.Resolution is not null)
        {
            report.Resolution = update.Resolution;
            report.UpdatedAt = now;
        }

        await Db.SaveChangesAsync(ct);

        return Ok(ReportDto.From(report, includeEvidence: true));
    }
}
