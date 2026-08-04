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

/// <summary>
/// The GDPR Art. 15 / 20 access-and-portability path (T1-7 of docs/specs/privacy.md).
///
/// <para><b>Internal routes.</b> These are <c>/api/v1/data-exports*</c> as this service sees them;
/// the gateway maps <c>/api/v1/identity/**</c> onto <c>/api/v1/**</c>, so a client calls
/// <c>/api/v1/identity/data-exports</c>. Same convention as every other controller here.</para>
///
/// <para><b>Asking is cheap; answering is not.</b> <c>POST</c> commits a row, publishes
/// <c>DataExportRequestedEvent</c> and returns <c>202</c>. Everything after that is Echo's
/// <c>ExportUserDataSaga</c> fanning out to eight services and Identity's
/// <c>AssembleUserDataExportCommandHandler</c> writing the zip - none of which happens on the request
/// thread, because an export reads every service's copy of a person's data and a subject holding an
/// HTTP connection open for it is a timeout, not a feature.</para>
///
/// <para><b>Every route here is audited.</b> The request and the download both write an
/// <c>IdentityAuditEvent</c> - see <c>IdentityAuditActions.DataExportRequested</c> and
/// <c>.DataExportDownloaded</c> for why the download in particular has to be as visible as a backup
/// read already is.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/data-exports")]
public class DataExportController(
    MicroserviceContext ctx,
    IDataExportArtifactStore store,
    IMessageBus bus,
    ILogger<DataExportController> logger) : ControllerBase
{
    /// <summary>
    /// Asks for a copy of the account's data. One request per account per 24 hours.
    /// </summary>
    /// <response code="202">Accepted. Poll <c>GET /api/v1/data-exports</c> for the status.</response>
    /// <response code="429">Another request was made inside the rate-limit window.</response>
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

        // Failed requests deliberately do not count. The limit exists because assembling an export is
        // expensive and because every live archive is another downloadable copy of somebody's
        // complete personal data - a request that produced neither is not what it is protecting
        // against, and making a subject wait out a day for a failure the system caused would turn an
        // internal fault into a statutory delay.
        //
        // Partial requests do not count either, for the same reason and one more. A partial export is
        // the same class of event as a failure - some service of ours was down - and it is the only
        // one of the two where the subject has been handed something that looks like an answer. If it
        // consumed the window, the system's fault would cost them a day AND leave them holding an
        // incomplete disclosure they were told to wait to re-request. The cost this limit guards is
        // real but bounded here: the archive that came back short is by definition the cheap one,
        // missing whole services' worth of rows, and it expires on the same seven-day clock.
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

        // After the commit, never before. The saga's first act is to fan out to eight services, one
        // of which is this one - and Identity's own participant handler looks this row up to flip it
        // to Running. Publishing first would race that read against the transaction that creates it.
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

    /// <summary>
    /// Redirects to a short-lived signed URL for the archive.
    /// </summary>
    /// <response code="302">Location carries the signed URL. Also the answer for a <c>Partial</c>
    /// export: the archive is short some service's section, but it is the subject's data and it is
    /// theirs to have - <c>GET /api/v1/data-exports</c> is where the incompleteness is stated.</response>
    /// <response code="404">No such export belongs to the caller.</response>
    /// <response code="409">The export exists but no archive was produced - still running, or failed.</response>
    /// <response code="410">The export's seven-day window has passed.</response>
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

        // Checked against the timestamp, not only against the status. The expiry sweep runs every few
        // hours; between the moment an archive's window closes and the moment the sweep notices, the
        // row still says Ready and the object is still in the bucket. The window is the promise.
        if (request.IsExpiredAt(now))
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                code = "data_export_expired",
                message = "This export has expired. Request a new one.",
                expiresAt = request.ExpiresAt,
            });
        }

        // Deliberately "is there an archive", not "is the status Ready". A Partial export has one,
        // and it holds everything the services that did answer hold about this person - gating it
        // behind Ready would answer an Art. 15 request with nothing because some other service was
        // down, which is the one outcome worse than answering it short. The incompleteness is stated
        // on the row and rendered by the list route; it is not a reason to withhold the data.
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
