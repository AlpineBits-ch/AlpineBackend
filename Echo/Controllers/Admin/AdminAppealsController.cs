using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Echo.Controllers.Admin;

/// <summary>The appeal queue.</summary>
[Authorize]
[Route("api/v1/admin/appeals")]
public class AdminAppealsController(
    MicroserviceContext context,
    StaffAccess staff,
    ModerationMailer mailer)
    : AdminControllerBase(context, staff)
{
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] AppealStatus? status,
        [FromQuery] bool openOnly,
        [FromQuery] int limit,
        [FromQuery] int offset,
        CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var query = Db.ModerationAppeals.AsNoTracking().Include(a => a.Action).AsQueryable();

        if (status is not null) query = query.Where(a => a.Status == status);
        if (openOnly)
        {
            query = query.Where(a => a.Status == AppealStatus.Pending || a.Status == AppealStatus.UnderReview);
        }

        var total = await query.CountAsync(ct);
        var (take, skip) = Page(limit, offset);
        var now = DateTimeOffset.UtcNow;

        var rows = await query
            // Undecided first, oldest first.
            .OrderBy(a => a.Status != AppealStatus.Pending && a.Status != AppealStatus.UnderReview)
            .ThenBy(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return Ok(new { total, appeals = rows.Select(a => AppealDto.From(a, now)) });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var appeal = await Db.ModerationAppeals.AsNoTracking()
            .Include(a => a.Action)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (appeal is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        // The report that produced the action, when there was one.
        ModerationReport? source = null;
        if (appeal.Action?.ReportId is { } reportId)
        {
            source = await Db.ModerationReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, ct);
        }

        var history = await Db.ModerationActions.AsNoTracking()
            .Where(a => appeal.Action != null && a.TargetUserId == appeal.Action.TargetUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        return Ok(new
        {
            appeal = AppealDto.From(appeal, now),
            report = source is null ? null : ReportDto.From(source, includeEvidence: true),
            history = history.Select(a => ActionDto.From(a, now)),
        });
    }

    /// <summary>Marks an appeal as being worked, so two moderators do not decide it twice.</summary>
    [HttpPost("{id}/claim")]
    public async Task<IActionResult> ClaimAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var appeal = await Db.ModerationAppeals.Include(a => a.Action).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appeal is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        appeal.Claim(actor.UserId, now);

        Audit(actor, ModerationAuditActions.AppealClaimed, appeal.Id);
        await Db.SaveChangesAsync(ct);

        return Ok(AppealDto.From(appeal, now));
    }

    [HttpPost("{id}/decide")]
    public async Task<IActionResult> DecideAsync(
        string id, [FromBody] DecideAppealRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        // Mandatory both ways.
        if (string.IsNullOrWhiteSpace(request.Note))
            return Failure(400, "note_required", "A decision requires a note explaining it.");

        var appeal = await Db.ModerationAppeals.Include(a => a.Action).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appeal is null) return NotFound();

        if (!appeal.IsOpen)
            return Failure(409, "already_decided", $"That appeal was already {appeal.Status}.");

        var now = DateTimeOffset.UtcNow;
        appeal.Decide(request.Granted, request.Note, actor.UserId, now);

        Audit(actor, ModerationAuditActions.AppealDecided, appeal.Id,
            $"{(request.Granted ? "accepted" : "declined")}, against {appeal.Action?.Reference ?? appeal.ActionId}");

        await Db.SaveChangesAsync(ct);

        if (request.Notify && appeal.Action is not null)
        {
            mailer.QueueAppealDecision(appeal, appeal.Action);
        }

        return Ok(new
        {
            appeal = AppealDto.From(appeal, now),
            // Said explicitly rather than left for the console to remember.
            followUpRequired = request.Granted,
            followUpMessage = request.Granted
                ? "The appeal is granted but the account is still restricted. Issue an unban from the user's page to restore it."
                : null,
        });
    }
}
