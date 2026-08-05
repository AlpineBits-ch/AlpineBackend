using System.Security.Claims;
using System.Text.Json;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Echo.RateLimiter;

namespace Echo.Controllers;

/// <summary>Where the clients file reports.</summary>
[Authorize]
[ApiController]
[Route("api/v1/reports")]
[EnableRateLimiting(GatewayRateLimiting.PolicyName)]
public class ReportsController(
    MicroserviceContext context,
    ILogger<ReportsController> logger) : ControllerBase
{
    /// <summary>How long a repeat report against the same subject folds into the first one.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(24);

    [HttpPost]
    public async Task<IActionResult> SubmitAsync([FromBody] SubmitReportRequest request, CancellationToken ct)
    {
        var reporterId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(reporterId)) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.TargetUserId))
            return Failure(400, "target_required", "A report needs a target account.");

        if (request.TargetUserId == reporterId)
            return Failure(400, "self_report", "You cannot report your own account.");

        if (!Enum.IsDefined(request.SubjectKind))
            return Failure(400, "subject_kind_invalid", "Unknown subject kind.");

        if (!Enum.IsDefined(request.Reason))
            return Failure(400, "reason_invalid", "Unknown reason.");

        // A message/channel/guild report with nothing identifying the thing being reported is a
        // report a moderator cannot act on. Refused here rather than accepted and dismissed later.
        if (request.SubjectKind != ReportSubjectKind.User && string.IsNullOrWhiteSpace(request.SubjectId))
            return Failure(400, "subject_id_required", $"A {request.SubjectKind} report needs a subject id.");

        string? evidence = null;
        if (request.Evidence is not null)
        {
            evidence = JsonSerializer.Serialize(request.Evidence);

            if (evidence.Length > ModerationReport.MaxEvidenceLength)
            {
                return Failure(400, "evidence_too_large",
                    $"The evidence snapshot must be under {ModerationReport.MaxEvidenceLength / 1024} KB.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var since = now - DuplicateWindow;

        // The same person reporting the same thing twice in a day is almost always one person
        // reporting one incident twice - a double tap, or a second thought with more detail.
        var existing = await context.ModerationReports.FirstOrDefaultAsync(r =>
            r.ReporterUserId == reporterId
            && r.TargetUserId == request.TargetUserId
            && r.SubjectKind == request.SubjectKind
            && r.SubjectId == request.SubjectId
            && (r.Status == ReportStatus.Open || r.Status == ReportStatus.Triaged)
            && r.CreatedAt >= since, ct);

        if (existing is not null)
        {
            var extra = (request.Details ?? string.Empty).Trim();
            if (extra.Length > 0 && !existing.Details.Contains(extra, StringComparison.Ordinal))
            {
                var merged = $"{existing.Details}\n\n--- added {now:u} ---\n{extra}";
                existing.Details = merged.Length > ModerationReport.MaxDetailsLength
                    ? merged[..ModerationReport.MaxDetailsLength]
                    : merged;
            }

            // A later report with a worse reason raises the priority; it never lowers it.
            var escalated = ModerationReport.PriorityFor(request.Reason);
            if (escalated > existing.Priority)
            {
                existing.Priority = escalated;
                existing.Reason = request.Reason;
            }

            existing.UpdatedAt = now;
            await context.SaveChangesAsync(ct);

            return Ok(new { id = existing.Id, status = existing.Status.ToString(), merged = true });
        }

        var report = ModerationReport.Create(new CreateReportParams
        {
            ReporterUserId = reporterId,
            TargetUserId = request.TargetUserId,
            SubjectKind = request.SubjectKind,
            SubjectId = request.SubjectId,
            Reason = request.Reason,
            Details = request.Details,
            EvidenceJson = evidence,
        }, now);

        context.ModerationReports.Add(report);
        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Report {ReportId} filed against {TargetUserId} ({Reason}, {Priority})",
            report.Id, report.TargetUserId, report.Reason, report.Priority);

        return Ok(new { id = report.Id, status = report.Status.ToString(), merged = false });
    }

    /// <summary>What this account has reported, and what came of it.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> MineAsync([FromQuery] int limit, CancellationToken ct)
    {
        var reporterId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(reporterId)) return Unauthorized();

        var take = Math.Clamp(limit <= 0 ? 25 : limit, 1, 100);

        var rows = await context.ModerationReports.AsNoTracking()
            .Where(r => r.ReporterUserId == reporterId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                SubjectKind = r.SubjectKind.ToString(),
                Reason = r.Reason.ToString(),
                r.CreatedAt,
                // Collapsed on purpose: "we acted" and "we did not" is the whole of what a reporter
                // gets. ActionTaken, Dismissed and Duplicate are staff distinctions.
                Status = r.Status == ReportStatus.Open || r.Status == ReportStatus.Triaged
                    ? "UnderReview"
                    : r.Status == ReportStatus.ActionTaken ? "ActionTaken" : "Closed",
                Resolved = r.ResolvedAt,
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    private IActionResult Failure(int status, string code, string message) =>
        StatusCode(status, new { code, message });
}
