using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;
using Echo.Persistence.Persistance;
using Echo.Sites;
using Microsoft.EntityFrameworkCore;

namespace Echo.Status;

/// <summary>The public payloads, held in memory and rebuilt on a timer.</summary>
public sealed class StatusSnapshot
{
    private volatile Holder _current = new(Empty(DateTimeOffset.UtcNow), EmptyUptime(DateTimeOffset.UtcNow), string.Empty);

    public StatusSummaryDto Summary => _current.Summary;
    public StatusUptimeDto Uptime => _current.Uptime;

    /// <summary>A cheap fingerprint of everything a client would notice.</summary>
    public string Signature => _current.Signature;

    public void Set(StatusSummaryDto summary, StatusUptimeDto uptime) =>
        _current = new Holder(summary, uptime, Fingerprint(summary));

    private sealed record Holder(StatusSummaryDto Summary, StatusUptimeDto Uptime, string Signature);

    private static string Fingerprint(StatusSummaryDto summary)
    {
        var parts = new List<string> { summary.Indicator };

        foreach (var component in summary.Components) parts.Add($"{component.Key}:{component.Status}");

        foreach (var incident in summary.Incidents.Concat(summary.Maintenance))
        {
            parts.Add($"{incident.Reference}:{incident.Status}:{incident.Updates.Count}");
        }

        return string.Join('|', parts);
    }

    public static StatusSummaryDto Empty(DateTimeOffset now) => new(
        StatusText.Slug(StatusIndicator.Operational), now, null, [], [], [], []);

    public static StatusUptimeDto EmptyUptime(DateTimeOffset now) => new(now, []);
}

/// <summary>Builds the snapshot from the database.</summary>
public static class StatusSnapshotBuilder
{
    public const int RecentIncidentCount = 7;

    public static async Task<(StatusSummaryDto Summary, StatusUptimeDto Uptime)> BuildAsync(
        MicroserviceContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var components = await db.StatusComponents.AsNoTracking()
            .OrderBy(c => c.Position)
            .ThenBy(c => c.Key)
            .ToListAsync(ct);

        var visible = components.Where(c => c.IsVisible).ToList();

        var open = await db.StatusIncidents.AsNoTracking()
            .Where(i => !i.IsRetracted && i.ResolvedAt == null)
            .Include(i => i.Updates)
            .Include(i => i.Components)
            .ToListAsync(ct);

        var recent = await db.StatusIncidents.AsNoTracking()
            .Where(i => !i.IsRetracted && i.ResolvedAt != null)
            .OrderByDescending(i => i.ResolvedAt)
            .Take(RecentIncidentCount)
            .Include(i => i.Components)
            .ToListAsync(ct);

        var since = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-StatusDayRollup.RetentionDays);

        var rollups = await db.StatusDayRollups.AsNoTracking()
            .Where(r => r.Day >= since)
            .ToListAsync(ct);

        var byComponent = rollups.GroupBy(r => r.ComponentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Day).ToList());

        var names = components.ToDictionary(c => c.Id, c => c.Key);

        var summary = new StatusSummaryDto(
            StatusText.Slug(StatusText.Indicator(visible.Select(c => c.Status))),
            now,
            Banner(open, names),
            [.. visible.Select(c => Component(c, byComponent.GetValueOrDefault(c.Id)))],
            [.. open.Where(i => i.Kind == IncidentKind.Incident)
                .OrderByDescending(i => i.StartedAt)
                .Select(i => Incident(i, names, includeUpdates: true))],
            [.. open.Where(i => i.Kind == IncidentKind.Maintenance)
                .OrderBy(i => i.ScheduledFor ?? i.StartedAt)
                .Select(i => Incident(i, names, includeUpdates: true))],
            [.. recent.Select(i => Incident(i, names, includeUpdates: false))]);

        var uptime = new StatusUptimeDto(now,
        [
            .. visible.Select(c => new StatusUptimeComponentDto(
                c.Key, c.Name,
                Uptime90d(byComponent.GetValueOrDefault(c.Id)),
                Days(byComponent.GetValueOrDefault(c.Id), now)))
        ]);

        return (summary, uptime);
    }

    /// <summary>
    /// The single incident a client puts on its banner: worst impact first, then most recent.
    /// </summary>
    private static StatusBannerDto? Banner(List<StatusIncident> open, Dictionary<string, string> names)
    {
        var chosen = open
            .Where(i => i.Kind == IncidentKind.Incident)
            .OrderByDescending(i => i.Impact)
            .ThenByDescending(i => i.StartedAt)
            .FirstOrDefault()
            ?? open.FirstOrDefault(i => i.Kind == IncidentKind.Maintenance && i.Status == IncidentStatus.InProgress);

        if (chosen is null) return null;

        var latest = chosen.Updates.MaxBy(u => u.PostedAt);

        return new StatusBannerDto(
            chosen.Title,
            latest?.Body ?? string.Empty,
            StatusText.Severity(chosen.Impact, chosen.Kind),
            chosen.Reference,
            IncidentUrl(chosen.Reference),
            chosen.Template,
            // Only when it is unambiguous.
            chosen.Components.Count == 1 ? names.GetValueOrDefault(chosen.Components.First().ComponentId) : null);
    }

    private static StatusComponentDto Component(StatusComponent component, List<StatusDayRollup>? rollups) =>
        new(component.Key, component.Name, component.Description,
            StatusText.Slug(component.Status), component.StatusSince, Uptime90d(rollups));

    public static StatusIncidentDto Incident(
        StatusIncident incident, Dictionary<string, string> componentKeys, bool includeUpdates) =>
        new(incident.Reference,
            StatusText.Slug(incident.Kind),
            incident.Title,
            StatusText.Slug(incident.Impact),
            StatusText.Slug(incident.Status),
            [.. incident.Components
                .Select(c => componentKeys.GetValueOrDefault(c.ComponentId))
                .Where(k => k is not null)
                .Select(k => k!)],
            incident.StartedAt,
            incident.ResolvedAt,
            incident.ScheduledFor,
            incident.ScheduledUntil,
            incident.Template,
            IncidentUrl(incident.Reference),
            includeUpdates
                ? [.. incident.Updates
                    .OrderByDescending(u => u.PostedAt)
                    .Select(u => new StatusUpdateDto(StatusText.Slug(u.Status), u.Body, u.Template, u.PostedAt))]
                : []);

    /// <summary>
    /// Absolute, and on the status host rather than wherever the caller happens to be.
    /// </summary>
    public static string IncidentUrl(string reference) =>
        $"{SiteHost.BaseUrl(SiteHosting.StatusHost)}/incident?ref={Uri.EscapeDataString(reference)}";

    private static double? Uptime90d(List<StatusDayRollup>? rollups)
    {
        if (rollups is null || rollups.Count == 0) return null;

        var accounted = rollups.Sum(r => r.AccountedSeconds);
        if (accounted <= 0) return null;

        return rollups.Sum(r => r.OperationalSeconds + r.MaintenanceSeconds + (r.DegradedSeconds * 0.5)) / accounted;
    }

    /// <summary>Ninety entries, oldest first, with the missing days filled in as nulls.</summary>
    private static IReadOnlyList<StatusUptimeDayDto> Days(List<StatusDayRollup>? rollups, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var byDay = rollups?.ToDictionary(r => r.Day) ?? [];
        var days = new List<StatusUptimeDayDto>(StatusDayRollup.RetentionDays);

        for (var offset = StatusDayRollup.RetentionDays - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);

            if (!byDay.TryGetValue(day, out var rollup))
            {
                days.Add(new StatusUptimeDayDto(day, null, "unknown"));
                continue;
            }

            days.Add(new StatusUptimeDayDto(day, rollup.Uptime, DayStatus(rollup)));
        }

        return days;
    }

    /// <summary>The bar's colour.</summary>
    private static string DayStatus(StatusDayRollup rollup)
    {
        if (rollup.OutageSeconds > 0) return StatusText.Slug(ComponentStatus.MajorOutage);
        if (rollup.DegradedSeconds > 0) return StatusText.Slug(ComponentStatus.DegradedPerformance);
        if (rollup.MaintenanceSeconds > 0) return StatusText.Slug(ComponentStatus.UnderMaintenance);
        return rollup.AccountedSeconds > 0 ? StatusText.Slug(ComponentStatus.Operational) : "unknown";
    }
}
