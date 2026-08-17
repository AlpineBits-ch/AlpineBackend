using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Persistence.Persistance;
using Echo.Sites;
using Echo.Status;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Echo.Controllers;

/// <summary>The public status API.</summary>
[ApiController]
[Route("api/v1/status")]
[EnableCors(StatusCors.PolicyName)]
[EnableRateLimiting(StatusRateLimiting.PolicyName)]
public class StatusController(
    StatusSnapshot snapshot,
    StatusOptions options,
    MicroserviceContext context)
    : ControllerBase
{
    /// <summary>Everything the page and every client needs, in one call.</summary>
    [HttpGet("summary")]
    public IActionResult Summary()
    {
        Cache();
        return Ok(snapshot.Summary);
    }

    /// <summary>The 90-day strip.</summary>
    [HttpGet("uptime")]
    public IActionResult Uptime()
    {
        Cache();
        return Ok(snapshot.Uptime);
    }

    /// <summary>Incident history.</summary>
    [HttpGet("incidents")]
    public async Task<IActionResult> ListAsync(
        [FromQuery] IncidentKind? kind,
        [FromQuery] int limit,
        [FromQuery] int offset,
        CancellationToken ct)
    {
        var query = PublicIncidents().AsQueryable();
        if (kind is not null) query = query.Where(i => i.Kind == kind);

        var total = await query.CountAsync(ct);

        var take = Math.Clamp(limit <= 0 ? 25 : limit, 1, 100);
        var skip = Math.Max(0, offset);

        var rows = await query
            .OrderByDescending(i => i.StartedAt)
            .Skip(skip)
            .Take(take)
            .Include(i => i.Components)
            .ToListAsync(ct);

        var keys = await ComponentKeysAsync(ct);

        return Ok(new
        {
            total,
            incidents = rows.Select(i => StatusSnapshotBuilder.Incident(i, keys, includeUpdates: false)),
        });
    }

    /// <summary>The permalink.</summary>
    [HttpGet("incidents/{reference}")]
    public async Task<IActionResult> GetAsync(string reference, CancellationToken ct)
    {
        var normalised = PublicReference.Normalise(reference);
        if (normalised is null) return NotFound(new { code = "not_found", message = "No such incident." });

        var incident = await PublicIncidents()
            .Include(i => i.Components)
            .Include(i => i.Updates)
            .FirstOrDefaultAsync(i => i.Reference == normalised, ct);

        if (incident is null) return NotFound(new { code = "not_found", message = "No such incident." });

        var keys = await ComponentKeysAsync(ct);

        return Ok(StatusSnapshotBuilder.Incident(incident, keys, includeUpdates: true));
    }

    /// <summary>An Atom feed of the last fifty incidents.</summary>
    [HttpGet("feed.atom")]
    public async Task<IActionResult> FeedAsync(CancellationToken ct)
    {
        var incidents = await PublicIncidents()
            .OrderByDescending(i => i.StartedAt)
            .Take(50)
            .Include(i => i.Updates)
            .ToListAsync(ct);

        var feed = StatusFeed.Render(
            incidents, SiteHost.BaseUrl(SiteHosting.StatusHost), DateTimeOffset.UtcNow);

        Cache();
        return Content(feed, "application/atom+xml; charset=utf-8");
    }

    /// <summary>Retracted incidents leave every public read here, in one place, rather than in each
    /// query - a filter that has to be remembered is a filter that will be forgotten.</summary>
    private IQueryable<Domain.Entities.Status.StatusIncident> PublicIncidents() =>
        context.StatusIncidents.AsNoTracking().Where(i => !i.IsRetracted);

    private async Task<Dictionary<string, string>> ComponentKeysAsync(CancellationToken ct) =>
        await context.StatusComponents.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Key, ct);

    private void Cache() =>
        Response.Headers.CacheControl =
            $"public, max-age={(int)options.PublicCacheDuration.TotalSeconds}";
}
