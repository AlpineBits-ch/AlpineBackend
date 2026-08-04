using System.Security.Claims;
using AppEnvironment;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Services;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Controllers;

/// <summary>
/// Versioned legal documents and the consent records against them (T1-10 / T1-12).
/// </summary>
[ApiController]
[Route("api/v1/legal")]
public class LegalController(
    MicroserviceContext ctx,
    ConsentService consents,
    LegalDocumentCatalog catalog,
    ILogger<LegalController> logger) : ControllerBase
{
    /// <summary>The current version of each published document.</summary>
    [AllowAnonymous]
    [HttpGet("documents")]
    [ProducesResponseType<IEnumerable<LegalDocumentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDocumentsAsync(CancellationToken ct)
    {
        var current = await consents.GetCurrentDocumentsAsync(DateTimeOffset.UtcNow, ct);
        return Ok(current.Select(LegalDocumentDto.From));
    }

    /// <summary>The bytes of one published version (T1-12).</summary>
    [AllowAnonymous]
    [HttpGet("documents/{documentType}/{version}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetDocumentContent(string documentType, string version)
    {
        if (!Enum.TryParse<LegalDocumentType>(documentType, ignoreCase: true, out var type))
            return NotFound();

        var content = catalog.ReadContent(type, version);
        if (content is null) return NotFound();

        Response.Headers.CacheControl = "public, max-age=3600";
        return File(content, "text/markdown; charset=utf-8");
    }

    /// <summary>What the caller has accepted, newest first.</summary>
    [Authorize]
    [HttpGet("consents")]
    [ProducesResponseType<IEnumerable<UserConsentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConsentsAsync(CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        var recorded = await consents.GetConsentsAsync(userId, ct);
        return Ok(recorded.Select(UserConsentDto.From));
    }

    /// <summary>
    /// Records the caller's acceptance of one document version, with the request's IP address.
    /// </summary>
    [Authorize]
    [HttpPost("consents")]
    [ProducesResponseType<UserConsentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordConsentAsync([FromBody] RecordConsentRequest request, CancellationToken ct)
    {
        var userId = User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();

        if (string.IsNullOrWhiteSpace(request.Version))
            return BadRequest("version is required.");

        var now = DateTimeOffset.UtcNow;
        var document = await ctx.LegalDocuments.AsNoTracking().FirstOrDefaultAsync(
            d => d.DocumentType == request.DocumentType && d.Version == request.Version, ct);

        if (document is null)
            return NotFound($"No published {request.DocumentType} document at version '{request.Version}'.");

        if (document.EffectiveAt > now)
            return BadRequest("That version is published but not yet in force.");

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var consent = await consents.RecordAsync(userId, request.DocumentType, request.Version, ip, now, ct);

        // Only audit a consent that is actually new.
        if (ctx.Entry(consent).State == EntityState.Added)
        {
            ctx.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
            {
                UserId = userId,
                Action = IdentityAuditActions.ConsentRecorded,
                Detail = $"{request.DocumentType} v{request.Version}",
                IpAddress = ip,
            }));

            logger.LogInformation("Recorded {DocumentType} v{Version} consent for {UserId}",
                request.DocumentType, request.Version, userId);
        }

        await ctx.SaveChangesAsync(ct);
        return Ok(UserConsentDto.From(consent));
    }

    /// <summary>
    /// Explicitly refuses withdrawal of Terms/Privacy consent, and says what to do instead.
    /// </summary>
    [Authorize]
    [HttpDelete("consents/{documentType}")]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult WithdrawConsent(string documentType)
    {
        return Conflict(new
        {
            code = "consent_not_withdrawable",
            message =
                "Acceptance of the terms and the privacy policy cannot be withdrawn while the account "
                + "is active - they are the basis on which the account exists. To withdraw, delete the "
                + "account (DELETE /api/v1/users/self), which starts a cancellable grace period and "
                + "then erases the account. Optional consents - data collection, personalization and "
                + "voice recording in clips - are separate and can be withdrawn at any time through "
                + "PATCH /api/v1/privacy-settings with no loss of service.",
            deleteAccount = "/api/v1/users/self",
            optionalConsents = "/api/v1/privacy-settings",
        });
    }
}
