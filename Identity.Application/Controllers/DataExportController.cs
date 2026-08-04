using System.Globalization;
using System.Security.Claims;
using AppEnvironment;
using Identity.Application.Dtos.Response;
using Identity.Application.Services.DataExport;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Identity.Application.Controllers;

/// <summary>The GDPR Art.</summary>
[Authorize]
[ApiController]
[Route("api/v1/data-exports")]
public class DataExportController(
    MicroserviceContext ctx,
    IDataExportArtifactStore store,
    IMessageBus bus,
    ILogger<DataExportController> logger) : ControllerBase
{
    /// <summary>Asks for a copy of the account's data.</summary>
    [HttpPost]
    [ProducesResponseType<DataExportRequestDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestAsync(CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var now = DateTimeOffset.UtcNow;
        var window = Env.DataExport.RateLimitWindow;
        var since = now - window;

        // Failed requests deliberately do not count.
        var recent = await ctx.DataExportRequests
            .Where(r => r.UserId == userId
                        && r.RequestedAt > since
                        && r.Status != DataExportStatus.Failed
                        && r.Status != DataExportStatus.Partial)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);

        if (recent is not null)
        {
            var retryAfter = recent.RequestedAt + window - now;
            var seconds = (int)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));

            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                code = "data_export_rate_limited",
                message = $"An export was already requested in the last {window.TotalHours:0.#} hours.",
                exportId = recent.Id,
                retryAfterSeconds = seconds,
            });
        }

        var request = DataExportRequest.Create(userId, now);
        ctx.DataExportRequests.Add(request);

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.DataExportRequested,
            Detail = request.Id,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now,
        }));

        await ctx.SaveChangesAsync(ct);

        // After the commit, never before.
        await bus.PublishAsync(new DataExportRequestedEvent
        {
            ExportId = request.Id,
            UserId = userId,
        });

        logger.LogInformation("Data export {ExportId} requested by {UserId}", request.Id, userId);

        return Accepted(DataExportRequestDto.From(request));
    }

    /// <summary>The account's own export requests, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<List<DataExportRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var rows = await ctx.DataExportRequests
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);

        return Ok(rows.Select(DataExportRequestDto.From).ToList());
    }

    /// <summary>Redirects to a short-lived signed URL for the archive.</summary>
    [HttpGet("{id}/download")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> DownloadAsync(string id, CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        // Scoped to the caller in the query itself, so somebody else's export id is a 404 and not a
        // 403 - a distinguishable refusal here would confirm that a given export id exists, which is
        // an oracle over other people's requests for their own data.
        var request = await ctx.DataExportRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);

        if (request is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        // Checked against the timestamp, not only against the status.
        if (request.IsExpiredAt(now))
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                code = "data_export_expired",
                message = "This export has expired. Request a new one.",
                expiresAt = request.ExpiresAt,
            });
        }

        // Deliberately "is there an archive", not "is the status Ready".
        if (!request.IsDownloadable)
        {
            return Conflict(new
            {
                code = "data_export_not_ready",
                message = "This export is not ready to download.",
                status = request.Status.ToString(),
                failureReason = request.FailureReason,
            });
        }

        // Non-null by IsDownloadable, which the compiler cannot see through a property.
        var url = await store.GetDownloadUrlAsync(request.ArtifactKey!, Env.DataExport.DownloadUrlLifetime, ct);

        ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.DataExportDownloaded,
            // The export id, never the artifact key - see IdentityAuditActions.DataExportDownloaded.
            Detail = request.Id,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now,
        }));

        // Committed before the redirect is handed back, so there is no path on which the archive
        // leaves and the record of it leaving does not.
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Data export {ExportId} downloaded by {UserId}", request.Id, userId);

        return Redirect(url);
    }
}
