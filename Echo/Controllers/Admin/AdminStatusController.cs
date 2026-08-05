using Echo.Domain.Entities.Moderation;
using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Echo.Realtime;
using Echo.Status;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Echo.Controllers.Admin;

/// <summary>
/// The status page's console surface: what the detector currently sees, and everything staff
/// publish.
/// </summary>
[Authorize]
[Route("api/v1/admin/status")]
public class AdminStatusController(
    MicroserviceContext context,
    StaffAccess staff,
    StatusSnapshot snapshot,
    StatusSignalBoard signals,
    IHubContext<EchoRealtimeHub> hub)
    : AdminControllerBase(context, staff)
{
    // ── Signals ───────────────────────────────────────────────────────────────────────────────

    /// <summary>What the detector sees right now, with the numbers.</summary>
    [HttpGet("signals")]
    public async Task<IActionResult> SignalsAsync()
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        return Ok(new { replica = Environment.MachineName, report = signals.Current });
    }

    // ── Components ────────────────────────────────────────────────────────────────────────────

    [HttpGet("components")]
    public async Task<IActionResult> ComponentsAsync(CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var components = await Db.StatusComponents.AsNoTracking()
            .OrderBy(c => c.Position)
            .ThenBy(c => c.Key)
            .ToListAsync(ct);

        return Ok(new { components = components.Select(AdminComponentDto.From) });
    }

    /// <summary>
    /// Adds a component the catalog does not ship - a third-party dependency, a marketing site,
    /// anything with no cluster behind it that staff drive by hand.
    /// </summary>
    [HttpPost("components")]
    public async Task<IActionResult> CreateComponentAsync(
        [FromBody] SaveComponentRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var key = request.Key?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(request.Name))
            return Failure(400, "invalid_component", "A component needs a key and a name.");

        // The key is what every client switches on and localises from, so it is fixed at creation
        // and there is no endpoint that changes it.
        if (!key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            return Failure(400, "invalid_key", "A component key may contain lowercase letters, digits, hyphens and underscores.");

        if (await Db.StatusComponents.AnyAsync(c => c.Key == key, ct))
            return Failure(409, "duplicate_key", "A component with that key already exists.");

        var now = DateTimeOffset.UtcNow;

        var component = StatusComponent.Create(
            new StatusComponentSeed(key, request.Name!.Trim(), request.Description?.Trim() ?? string.Empty,
                request.ImpactHint?.Trim() ?? string.Empty, request.Clusters ?? [], request.Position ?? 500),
            now);

        component.IsVisible = request.IsVisible ?? true;

        Db.StatusComponents.Add(component);
        Audit(actor, ModerationAuditActions.StatusComponentCreated, component.Id, key);

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct);

        return Ok(AdminComponentDto.From(component));
    }

    [HttpPatch("components/{id}")]
    public async Task<IActionResult> UpdateComponentAsync(
        string id, [FromBody] SaveComponentRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var component = await Db.StatusComponents.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (component is null) return NotFound();

        if (request.Name is not null) component.Name = request.Name.Trim();
        if (request.Description is not null) component.Description = request.Description.Trim();
        if (request.ImpactHint is not null) component.ImpactHint = request.ImpactHint.Trim();
        if (request.Clusters is not null) component.Clusters = [.. request.Clusters];
        if (request.Position is not null) component.Position = request.Position.Value;
        if (request.IsVisible is not null) component.IsVisible = request.IsVisible.Value;

        // Sent as a percentage by the console, stored as a fraction.
        if (request.DegradedRate is not null) component.DegradedRate = Threshold(request.DegradedRate.Value);
        if (request.OutageRate is not null) component.OutageRate = Threshold(request.OutageRate.Value);
        if (request.MinimumVolume is not null)
            component.MinimumVolume = request.MinimumVolume.Value > 0 ? request.MinimumVolume.Value : null;

        component.UpdatedAt = DateTimeOffset.UtcNow;

        Audit(actor, ModerationAuditActions.StatusComponentUpdated, component.Id, component.Key);

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct);

        return Ok(AdminComponentDto.From(component));
    }

    // ── Incidents ─────────────────────────────────────────────────────────────────────────────

    [HttpGet("incidents")]
    public async Task<IActionResult> ListIncidentsAsync(
        [FromQuery] IncidentKind? kind,
        [FromQuery] bool openOnly,
        [FromQuery] bool includeRetracted,
        [FromQuery] int limit,
        [FromQuery] int offset,
        CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var query = Db.StatusIncidents.AsNoTracking().AsQueryable();

        if (!includeRetracted) query = query.Where(i => !i.IsRetracted);
        if (kind is not null) query = query.Where(i => i.Kind == kind);
        if (openOnly) query = query.Where(i => i.ResolvedAt == null);

        var total = await query.CountAsync(ct);
        var (take, skip) = Page(limit, offset);

        var rows = await query
            // Open first, then newest.
            .OrderBy(i => i.ResolvedAt != null)
            .ThenByDescending(i => i.StartedAt)
            .Skip(skip)
            .Take(take)
            .Include(i => i.Components)
            .ToListAsync(ct);

        var keys = await ComponentKeysAsync(ct);

        return Ok(new
        {
            total,
            // Surfaced separately so the console can badge it: a generated incident nobody has
            // acknowledged is the one thing on this page that is waiting for a person.
            unconfirmed = await Db.StatusIncidents
                .CountAsync(i => !i.IsRetracted && i.ResolvedAt == null
                                 && i.Origin == IncidentOrigin.Automatic && !i.Confirmed, ct),
            incidents = rows.Select(i => AdminIncidentDto.From(i, keys, includeUpdates: false)),
        });
    }

    [HttpGet("incidents/{id}")]
    public async Task<IActionResult> GetIncidentAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var incident = await Db.StatusIncidents.AsNoTracking()
            .Include(i => i.Components)
            .Include(i => i.Updates)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (incident is null) return NotFound();

        return Ok(AdminIncidentDto.From(incident, await ComponentKeysAsync(ct), includeUpdates: true));
    }

    [HttpPost("incidents")]
    public async Task<IActionResult> CreateIncidentAsync(
        [FromBody] CreateIncidentRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        if (string.IsNullOrWhiteSpace(request.Title))
            return Failure(400, "title_required", "An incident needs a title.");

        // Mandatory, and the one piece of validation worth arguing for: an incident with no first
        // update is a red banner with nothing under it, which is worse for a reader than silence.
        if (string.IsNullOrWhiteSpace(request.Body))
            return Failure(400, "body_required", "An incident needs a first update explaining it.");

        var status = request.Status ?? DefaultStatus(request.Kind);
        if (!IsValidStatus(request.Kind, status))
            return InvalidStatus(request.Kind, status);

        if (request.Kind == IncidentKind.Maintenance && request.ScheduledFor is null)
            return Failure(400, "schedule_required", "Scheduled maintenance needs a start time.");

        var (components, missing) = await ResolveComponentsAsync(request.Components, ct);
        if (missing is not null) return missing;

        var now = DateTimeOffset.UtcNow;

        var incident = StatusIncident.Create(new CreateIncidentParams
        {
            Kind = request.Kind,
            Title = request.Title!,
            Body = request.Body!,
            Impact = request.Impact ?? IncidentImpact.Minor,
            Status = status,
            ScheduledFor = request.ScheduledFor,
            ScheduledUntil = request.ScheduledUntil,
            CreatedByUserId = actor.UserId,
        }, now);

        incident.SetComponents(
            components.Select(c => (c.Id, StatusText.AssertedStatus(incident.Impact, incident.Kind))), now);

        Db.StatusIncidents.Add(incident);
        Audit(actor, ModerationAuditActions.StatusIncidentCreated, incident.Id,
            $"{incident.Kind} {incident.Reference}: {incident.Title}");

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct, incident.Id);

        return Ok(AdminIncidentDto.From(incident, await ComponentKeysAsync(ct), includeUpdates: true));
    }

    /// <summary>Posts to the timeline and moves the lifecycle state.</summary>
    [HttpPost("incidents/{id}/updates")]
    public async Task<IActionResult> PostUpdateAsync(
        string id, [FromBody] PostUpdateRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        if (string.IsNullOrWhiteSpace(request.Body))
            return Failure(400, "body_required", "An update needs a body.");

        var incident = await Db.StatusIncidents
            .Include(i => i.Components)
            .Include(i => i.Updates)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (incident is null) return NotFound();

        var status = request.Status ?? incident.Status;
        if (!IsValidStatus(incident.Kind, status)) return InvalidStatus(incident.Kind, status);

        var now = DateTimeOffset.UtcNow;
        var reopening = !incident.IsOpen && status is not (IncidentStatus.Resolved or IncidentStatus.Completed or IncidentStatus.Cancelled);

        incident.PostUpdate(status, request.Body!, template: null, actor.UserId, now);

        // The assertion follows the incident: an incident that has closed asserts nothing, so its
        // components go back to whatever else is still claiming them.
        incident.SetComponents(
            incident.IsOpen
                ? incident.Components.Select(c => (c.ComponentId, StatusText.AssertedStatus(incident.Impact, incident.Kind))).ToList()
                : [],
            now);

        Audit(actor, ModerationAuditActions.StatusIncidentUpdated, incident.Id,
            $"{incident.Reference} -> {status}{(reopening ? " (reopened)" : string.Empty)}");

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct, incident.Id);

        return Ok(AdminIncidentDto.From(incident, await ComponentKeysAsync(ct), includeUpdates: true));
    }

    /// <summary>Retitle, re-impact, or re-scope.</summary>
    [HttpPatch("incidents/{id}")]
    public async Task<IActionResult> EditIncidentAsync(
        string id, [FromBody] EditIncidentRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var incident = await Db.StatusIncidents
            .Include(i => i.Components)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (incident is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Title)) incident.Title = request.Title!.Trim();
        if (request.Impact is not null) incident.Impact = request.Impact.Value;
        if (request.ScheduledFor is not null) incident.ScheduledFor = request.ScheduledFor;
        if (request.ScheduledUntil is not null) incident.ScheduledUntil = request.ScheduledUntil;

        if (request.Components is not null)
        {
            var (components, missing) = await ResolveComponentsAsync(request.Components, ct);
            if (missing is not null) return missing;

            incident.SetComponents(
                incident.IsOpen
                    ? components.Select(c => (c.Id, StatusText.AssertedStatus(incident.Impact, incident.Kind)))
                    : [],
                now);
        }
        else if (incident.IsOpen && request.Impact is not null)
        {
            // Impact drives what the incident claims about its components, so changing one without
            // the other would leave a critical incident asserting "degraded".
            incident.SetComponents(
                incident.Components.Select(c => (c.ComponentId, StatusText.AssertedStatus(incident.Impact, incident.Kind))).ToList(),
                now);
        }

        // Taking the pen counts as taking ownership, even when the edit is cosmetic.
        incident.Confirm(now);
        incident.UpdatedAt = now;

        Audit(actor, ModerationAuditActions.StatusIncidentEdited, incident.Id, incident.Reference);

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct, incident.Id);

        return Ok(AdminIncidentDto.From(incident, await ComponentKeysAsync(ct), includeUpdates: false));
    }

    /// <summary>Takes ownership of a generated incident without posting anything.</summary>
    [HttpPost("incidents/{id}/confirm")]
    public async Task<IActionResult> ConfirmAsync(string id, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var incident = await Db.StatusIncidents
            .Include(i => i.Components)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (incident is null) return NotFound();

        incident.Confirm(DateTimeOffset.UtcNow);

        Audit(actor, ModerationAuditActions.StatusIncidentConfirmed, incident.Id, incident.Reference);

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct, incident.Id);

        return Ok(AdminIncidentDto.From(incident, await ComponentKeysAsync(ct), includeUpdates: false));
    }

    /// <summary>Takes an incident off the public page without deleting it.</summary>
    [HttpPost("incidents/{id}/retract")]
    public async Task<IActionResult> RetractAsync(
        string id, [FromBody] RetractRequest? request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();
        if (!actor.IsAdmin) return AdminOnly();

        var incident = await Db.StatusIncidents
            .Include(i => i.Components)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (incident is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        incident.Retract(now);
        incident.Confirm(now);
        incident.SetComponents([], now);
        incident.AppendDetectionDetail($"[{now:u}] retracted by {actor.UserId}: {request?.Reason ?? "no reason given"}", now);

        Audit(actor, ModerationAuditActions.StatusIncidentRetracted, incident.Id,
            $"{incident.Reference}: {request?.Reason ?? "no reason given"}");

        await Db.SaveChangesAsync(ct);
        await RefreshAsync(ct, incident.Id);

        return Ok(AdminIncidentDto.From(incident, await ComponentKeysAsync(ct), includeUpdates: false));
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────

    private static IncidentStatus DefaultStatus(IncidentKind kind) =>
        kind == IncidentKind.Maintenance ? IncidentStatus.Scheduled : IncidentStatus.Investigating;

    /// <summary>The two lifecycles share one column and do not share values.</summary>
    private static bool IsValidStatus(IncidentKind kind, IncidentStatus status) => kind switch
    {
        IncidentKind.Maintenance => status is IncidentStatus.Scheduled or IncidentStatus.InProgress
            or IncidentStatus.Completed or IncidentStatus.Cancelled,
        _ => status is IncidentStatus.Investigating or IncidentStatus.Identified
            or IncidentStatus.Monitoring or IncidentStatus.Resolved,
    };

    private IActionResult InvalidStatus(IncidentKind kind, IncidentStatus status) =>
        Failure(400, "invalid_status", $"{status} is not a state a {kind.ToString().ToLowerInvariant()} can be in.");

    private static double? Threshold(double percent) =>
        percent > 0 ? Math.Clamp(percent / 100d, 0.0001, 1d) : null;

    private async Task<(List<StatusComponent> Components, IActionResult? Failure)> ResolveComponentsAsync(
        IReadOnlyList<string>? keys, CancellationToken ct)
    {
        if (keys is null || keys.Count == 0) return ([], null);

        var wanted = keys.Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList();
        var found = await Db.StatusComponents.Where(c => wanted.Contains(c.Key)).ToListAsync(ct);

        var unknown = wanted.Except(found.Select(c => c.Key)).ToArray();

        return unknown.Length == 0
            ? (found, null)
            : ([], Failure(400, "unknown_component", $"No such component: {string.Join(", ", unknown)}."));
    }

    private async Task<Dictionary<string, string>> ComponentKeysAsync(CancellationToken ct) =>
        await Db.StatusComponents.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Key, ct);

    /// <summary>Rebuilds the public snapshot and tells connected clients.</summary>
    private async Task RefreshAsync(CancellationToken ct, string? incidentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var (summary, uptime) = await StatusSnapshotBuilder.BuildAsync(Db, now, ct);

        snapshot.Set(summary, uptime);

        await hub.Clients.All.SendAsync(StatusRealtimeEvents.SummaryChanged, summary, ct);

        if (incidentId is null) return;

        var incident = await Db.StatusIncidents.AsNoTracking()
            .Include(i => i.Components)
            .Include(i => i.Updates)
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);

        // A retracted incident is pushed as nothing rather than as itself: clients merge by
        // reference and would otherwise keep showing what we just took down.
        if (incident is null || incident.IsRetracted) return;

        await hub.Clients.All.SendAsync(StatusRealtimeEvents.IncidentUpdated,
            new { incident = StatusSnapshotBuilder.Incident(incident, await ComponentKeysAsync(ct), includeUpdates: true) },
            ct);
    }
}

// ── Requests ──────────────────────────────────────────────────────────────────────────────────

public sealed class SaveComponentRequest
{
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ImpactHint { get; set; }
    public List<string>? Clusters { get; set; }
    public int? Position { get; set; }
    public bool? IsVisible { get; set; }

    /// <summary>Percentages, as the console shows them. Zero clears the override.</summary>
    public double? DegradedRate { get; set; }

    public double? OutageRate { get; set; }
    public int? MinimumVolume { get; set; }
}

public sealed class CreateIncidentRequest
{
    public IncidentKind Kind { get; set; } = IncidentKind.Incident;
    public string? Title { get; set; }
    public string? Body { get; set; }
    public IncidentImpact? Impact { get; set; }
    public IncidentStatus? Status { get; set; }

    /// <summary>Component keys, not ids.</summary>
    public List<string>? Components { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? ScheduledUntil { get; set; }
}

public sealed class PostUpdateRequest
{
    public string? Body { get; set; }
    public IncidentStatus? Status { get; set; }
}

public sealed class EditIncidentRequest
{
    public string? Title { get; set; }
    public IncidentImpact? Impact { get; set; }
    public List<string>? Components { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? ScheduledUntil { get; set; }
}

public sealed class RetractRequest
{
    public string? Reason { get; set; }
}

// ── Staff-facing DTOs ─────────────────────────────────────────────────────────────────────────

/// <summary>Carries what the public DTOs deliberately do not: the clusters behind a component and
/// the thresholds it is judged by.</summary>
public sealed record AdminComponentDto(
    string Id, string Key, string Name, string? Description, string ImpactHint,
    IReadOnlyList<string> Clusters, int Position, bool IsVisible,
    string Status, DateTimeOffset StatusSince,
    double? DegradedRate, double? OutageRate, int? MinimumVolume)
{
    public static AdminComponentDto From(StatusComponent c) =>
        new(c.Id, c.Key, c.Name, c.Description, c.ImpactHint, c.Clusters, c.Position, c.IsVisible,
            StatusText.Slug(c.Status), c.StatusSince, c.DegradedRate, c.OutageRate, c.MinimumVolume);
}

/// <summary>The incident as staff see it, including the detection detail that never leaves this
/// controller.</summary>
public sealed record AdminIncidentDto(
    string Id, string Reference, string Kind, string Title, string Impact, string Status,
    string Origin, string? Template, bool Confirmed, bool IsRetracted,
    IReadOnlyList<string> Components,
    DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt,
    DateTimeOffset? ScheduledFor, DateTimeOffset? ScheduledUntil,
    string? DetectionDetail, string? CreatedByUserId, string Url,
    IReadOnlyList<AdminUpdateDto> Updates)
{
    public static AdminIncidentDto From(
        StatusIncident i, Dictionary<string, string> componentKeys, bool includeUpdates) =>
        new(i.Id, i.Reference, StatusText.Slug(i.Kind), i.Title, StatusText.Slug(i.Impact),
            StatusText.Slug(i.Status), StatusText.Slug(i.Origin), i.Template, i.Confirmed, i.IsRetracted,
            [.. i.Components.Select(c => componentKeys.GetValueOrDefault(c.ComponentId)).Where(k => k is not null).Select(k => k!)],
            i.StartedAt, i.ResolvedAt, i.ScheduledFor, i.ScheduledUntil,
            i.DetectionDetail, i.CreatedByUserId, StatusSnapshotBuilder.IncidentUrl(i.Reference),
            includeUpdates
                ? [.. i.Updates.OrderByDescending(u => u.PostedAt)
                    .Select(u => new AdminUpdateDto(StatusText.Slug(u.Status), u.Body, u.Template, u.AuthorUserId, u.PostedAt))]
                : []);
}

public sealed record AdminUpdateDto(
    string Status, string Body, string? Template, string? AuthorUserId, DateTimeOffset PostedAt);
