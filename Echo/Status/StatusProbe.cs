using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;
using Echo.Persistence.Persistance;
using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

namespace Echo.Status;

/// <summary>The detector.</summary>
public sealed class StatusProbe(
    IServiceScopeFactory scopeFactory,
    StatusMetrics metrics,
    StatusOptions options,
    StatusSnapshot snapshot,
    StatusSignalBoard signals,
    IProxyStateLookup proxy,
    IHubContext<EchoRealtimeHub> hub,
    ILogger<StatusProbe> logger)
    : BackgroundService
{
    /// <summary>Postgres advisory lock id for the rollup writer.</summary>
    private const long RollupLockId = 8_724_531_001;

    private readonly Dictionary<string, ComponentProbeState> _states = new(StringComparer.Ordinal);
    private DateOnly _lastPrunedDay = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StatusSeeder.WarnAboutDrift(StatusSeeder.ClusterIds(proxy), logger);

        // The gateway migrates on startup before the host runs, so the tables exist by the time
        // this service starts.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

                var added = await StatusSeeder.EnsureAsync(db, DateTimeOffset.UtcNow, stoppingToken);
                if (added > 0) logger.LogInformation("Seeded {Count} status component(s)", added);

                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Status component seed failed; retrying in {Delay}", options.Interval);
                await Task.Delay(options.Interval, stoppingToken);
            }
        }

        using var timer = new PeriodicTimer(options.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never fatal.
                logger.LogError(ex, "Status probe tick failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var components = await db.StatusComponents.OrderBy(c => c.Position).ThenBy(c => c.Key).ToListAsync(ct);
        if (components.Count == 0) return;

        var open = await db.StatusIncidents
            .Where(i => !i.IsRetracted && i.ResolvedAt == null)
            .Include(i => i.Components)
            .Include(i => i.Updates)
            .ToListAsync(ct);

        // A component inside an announced maintenance window is not a component to open an incident
        // about.
        var suppressed = open
            .Where(i => i.Kind == IncidentKind.Maintenance && i.Status == IncidentStatus.InProgress)
            .SelectMany(i => i.Components)
            .Select(c => c.ComponentId)
            .ToHashSet(StringComparer.Ordinal);

        var automatic = open
            .Where(i => i.AutoComponentId is not null)
            .GroupBy(i => i.AutoComponentId!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var report = new List<ComponentSignal>(components.Count);
        var pendingCreations = new List<PendingIncident>();

        foreach (var component in components)
        {
            var reading = Measure(component);
            var verdict = StatusDetector.Classify(reading, Thresholds(component));

            var state = _states.TryGetValue(component.Id, out var existing)
                ? existing
                : _states[component.Id] = new ComponentProbeState();

            state.Apply(verdict);

            var incident = automatic.GetValueOrDefault(component.Id);

            if (options.AutoDetectionEnabled && !suppressed.Contains(component.Id))
            {
                if (StatusDetector.ShouldOpen(verdict, state, options))
                {
                    if (incident is null)
                    {
                        pendingCreations.Add(new PendingIncident(component.Id, verdict, Describe(component, reading, verdict)));
                    }
                    else
                    {
                        Escalate(incident, component, verdict, now);
                    }
                }
                else if (StatusDetector.ShouldRecover(verdict, state, options) && incident is not null)
                {
                    Recover(incident, component, now);
                }
            }

            report.Add(new ComponentSignal(
                component.Key, component.Name, StatusText.Slug(component.Status),
                reading.Sample.Total, reading.Sample.Errors, reading.Sample.ErrorRate,
                reading.Destinations, reading.Unhealthy,
                verdict.ToString().ToLowerInvariant(), state.BadStreak, state.CleanStreak,
                suppressed.Contains(component.Id),
                component.Clusters.Count > 0,
                incident?.Reference));
        }

        await db.SaveChangesAsync(ct);

        foreach (var creation in pendingCreations) await OpenAsync(creation, ct);

        var previous = components.ToDictionary(c => c.Id, c => c.Status, StringComparer.Ordinal);
        await ApplyComponentStatusesAsync(db, components, now, ct);
        await WriteRollupsAsync(db, components, previous, now, ct);

        await PublishAsync(db, now, ct);

        signals.Set(new StatusSignalReport(
            now, options.AutoDetectionEnabled,
            (int)(options.Interval.TotalSeconds * options.WindowBuckets),
            options.MinimumVolume, options.DegradedRate, options.OutageRate, options.RecoveryRate,
            options.OpenSamples, options.RecoverySamples, report));
    }

    // ── Measurement ───────────────────────────────────────────────────────────────────────────

    private StatusThresholds Thresholds(StatusComponent component) =>
        StatusThresholds.For(options, component.DegradedRate, component.OutageRate, component.MinimumVolume);

    private StatusReading Measure(StatusComponent component)
    {
        if (component.Clusters.Count == 0)
        {
            // Gateway-local.
            return new StatusReading(metrics.Read(StatusMetrics.LocalKey(component.Key)), 0, 0);
        }

        var destinations = 0;
        var unhealthy = 0;

        foreach (var clusterId in component.Clusters)
        {
            if (!proxy.TryGetCluster(clusterId, out var cluster)) continue;

            foreach (var destination in cluster.DestinationsState.AllDestinations)
            {
                destinations++;

                if (destination.Health.Active == DestinationHealth.Unhealthy
                    || destination.Health.Passive == DestinationHealth.Unhealthy)
                {
                    unhealthy++;
                }
            }
        }

        return new StatusReading(metrics.Read(component.Clusters), destinations, unhealthy);
    }

    private string Describe(StatusComponent component, StatusReading reading, StatusVerdict verdict) =>
        $"{verdict} on {component.Key} over {options.Interval.TotalSeconds * options.WindowBuckets:0}s: "
        + $"{reading.Sample.Errors}/{reading.Sample.Total} responses failed ({reading.Sample.ErrorRate:P1}); "
        + $"{reading.Unhealthy}/{reading.Destinations} destinations unhealthy; "
        + $"clusters [{string.Join(", ", component.Clusters)}]; replica {Environment.MachineName}";

    // ── Incident lifecycle ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an automatic incident, in its own scope so a losing race cannot roll back the tick.
    /// </summary>
    private async Task OpenAsync(PendingIncident pending, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var component = await db.StatusComponents.FirstOrDefaultAsync(c => c.Id == pending.ComponentId, ct);
        if (component is null) return;

        var template = pending.Verdict == StatusVerdict.Outage ? IncidentTemplates.Unavailable : IncidentTemplates.ElevatedErrors;
        var impact = pending.Verdict == StatusVerdict.Outage ? IncidentImpact.Major : IncidentImpact.Minor;
        var asserted = pending.Verdict == StatusVerdict.Outage ? ComponentStatus.MajorOutage : ComponentStatus.DegradedPerformance;

        // Within the reopen window, the same component appends to its last incident instead of
        // starting another.
        var reopenable = await db.StatusIncidents
            .Include(i => i.Components)
            .Where(i => i.AutoComponentId == component.Id
                        && !i.IsRetracted
                        && i.Origin == IncidentOrigin.Automatic
                        && i.ResolvedAt != null
                        && i.ResolvedAt > now - options.ReopenWindow)
            .OrderByDescending(i => i.ResolvedAt)
            .FirstOrDefaultAsync(ct);

        try
        {
            if (reopenable is not null)
            {
                // Reopening a confirmed incident would be the probe overruling a person who decided
                // it was over. It gets a note instead, and a fresh incident below.
                if (reopenable.CanBeAutomaticallyClosed)
                {
                    reopenable.PostUpdate(IncidentStatus.Investigating, IncidentTemplates.Body(template, component),
                        template, authorUserId: null, now);
                    reopenable.Impact = reopenable.Impact >= impact ? reopenable.Impact : impact;
                    reopenable.Template = template;
                    reopenable.AppendDetectionDetail($"[{now:u}] reopened. {pending.Detail}", now);
                    reopenable.SetComponents([(component.Id, asserted)], now);

                    await db.SaveChangesAsync(ct);
                    logger.LogWarning("Reopened automatic incident {Reference} for {Component}", reopenable.Reference, component.Key);
                    return;
                }

                reopenable.AppendDetectionDetail($"[{now:u}] recurrence after a confirmed resolution. {pending.Detail}", now);
            }

            var incident = StatusIncident.CreateAutomatic(component, template, impact, $"[{now:u}] {pending.Detail}", now);
            incident.SetComponents([(component.Id, asserted)], now);

            db.StatusIncidents.Add(incident);
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Opened automatic incident {Reference} for {Component}: {Detail}",
                incident.Reference, component.Key, pending.Detail);
        }
        catch (DbUpdateException ex)
        {
            // Expected under multiple replicas: somebody else opened it first.
            logger.LogDebug(ex, "Automatic incident for {Component} was opened by another replica", component.Key);
        }
    }

    /// <summary>Degraded has become an outage. Says so once, and only while nobody owns it.</summary>
    private void Escalate(StatusIncident incident, StatusComponent component, StatusVerdict verdict, DateTimeOffset now)
    {
        if (!incident.CanBeAutomaticallyClosed) return;
        if (verdict != StatusVerdict.Outage || incident.Template == IncidentTemplates.Unavailable) return;

        incident.PostUpdate(IncidentStatus.Investigating,
            IncidentTemplates.Body(IncidentTemplates.Unavailable, component),
            IncidentTemplates.Unavailable, authorUserId: null, now);

        incident.Title = IncidentTemplates.Title(IncidentTemplates.Unavailable, component);
        incident.Template = IncidentTemplates.Unavailable;
        incident.Impact = IncidentImpact.Major;
        incident.SetComponents([(component.Id, ComponentStatus.MajorOutage)], now);
        incident.AppendDetectionDetail($"[{now:u}] escalated to outage", now);
    }

    /// <summary>The signal is clean again.</summary>
    private void Recover(StatusIncident incident, StatusComponent component, DateTimeOffset now)
    {
        if (!incident.CanBeAutomaticallyClosed)
        {
            incident.AppendDetectionDetail($"[{now:u}] signal recovered on {component.Key}; left open for staff", now);
            return;
        }

        incident.PostUpdate(IncidentStatus.Resolved,
            IncidentTemplates.Body(IncidentTemplates.Recovered, component),
            IncidentTemplates.Recovered, authorUserId: null, now);

        if (StatusDetector.ShouldRetract(now - incident.StartedAt, options))
        {
            // Short, self-healed, and nobody looked at it.
            incident.Retract(now);
            incident.AppendDetectionDetail($"[{now:u}] retracted: lasted under {options.RetractionThreshold.TotalSeconds:0}s", now);

            logger.LogInformation(
                "Retracted automatic incident {Reference} for {Component} after {Seconds:0}s",
                incident.Reference, component.Key, (now - incident.StartedAt).TotalSeconds);

            return;
        }

        logger.LogInformation("Resolved automatic incident {Reference} for {Component}", incident.Reference, component.Key);
    }

    // ── Derived state ─────────────────────────────────────────────────────────────────────────

    /// <summary>A component is whatever the open incidents say it is.</summary>
    private static async Task ApplyComponentStatusesAsync(
        MicroserviceContext db, List<StatusComponent> components, DateTimeOffset now, CancellationToken ct)
    {
        var assertions = await db.StatusIncidents.AsNoTracking()
            .Where(i => !i.IsRetracted && i.ResolvedAt == null)
            .SelectMany(i => i.Components)
            .Select(c => new { c.ComponentId, c.Status })
            .ToListAsync(ct);

        var byComponent = assertions
            .GroupBy(a => a.ComponentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Status).ToArray(), StringComparer.Ordinal);

        foreach (var component in components)
        {
            component.SetStatus(Worst(byComponent.GetValueOrDefault(component.Id)), now);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Maintenance only wins when nothing worse is claimed, for the same reason the page's
    /// overall indicator works that way.</summary>
    private static ComponentStatus Worst(ComponentStatus[]? claims)
    {
        if (claims is null || claims.Length == 0) return ComponentStatus.Operational;

        var worst = ComponentStatus.Operational;
        var maintenance = false;

        foreach (var claim in claims)
        {
            if (claim == ComponentStatus.UnderMaintenance)
            {
                maintenance = true;
                continue;
            }

            if (Rank(claim) > Rank(worst)) worst = claim;
        }

        return worst == ComponentStatus.Operational && maintenance ? ComponentStatus.UnderMaintenance : worst;
    }

    private static int Rank(ComponentStatus status) => status switch
    {
        ComponentStatus.MajorOutage => 3,
        ComponentStatus.PartialOutage => 2,
        ComponentStatus.DegradedPerformance => 1,
        _ => 0,
    };

    private async Task WriteRollupsAsync(
        MicroserviceContext db, List<StatusComponent> components,
        Dictionary<string, ComponentStatus> previous, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var acquired = await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({RollupLockId}) AS \"Value\"")
            .SingleAsync(ct);

        if (!acquired)
        {
            await transaction.RollbackAsync(ct);
            return;
        }

        var rows = await db.StatusDayRollups.Where(r => r.Day == today).ToListAsync(ct);
        var byComponent = rows.ToDictionary(r => r.ComponentId, StringComparer.Ordinal);

        // One interval per successful tick, not the wall-clock gap since this replica last ran.
        var seconds = options.Interval.TotalSeconds;

        foreach (var component in components)
        {
            if (!byComponent.TryGetValue(component.Id, out var rollup))
            {
                rollup = StatusDayRollup.Create(component.Id, today, now);
                db.StatusDayRollups.Add(rollup);
                byComponent[component.Id] = rollup;
            }

            rollup.Add(previous.GetValueOrDefault(component.Id, component.Status), seconds, now);
        }

        if (_lastPrunedDay != today)
        {
            var cutoff = today.AddDays(-StatusDayRollup.RetentionDays);
            await db.StatusDayRollups.Where(r => r.Day < cutoff).ExecuteDeleteAsync(ct);
            _lastPrunedDay = today;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task PublishAsync(MicroserviceContext db, DateTimeOffset now, CancellationToken ct)
    {
        var previous = snapshot.Signature;
        var (summary, uptime) = await StatusSnapshotBuilder.BuildAsync(db, now, ct);

        snapshot.Set(summary, uptime);

        if (snapshot.Signature == previous) return;

        // Signed-in clients only - the hub requires authentication, so the anonymous status page
        // polls instead.
        await hub.Clients.All.SendAsync(StatusRealtimeEvents.SummaryChanged, summary, ct);
    }

    private sealed record PendingIncident(string ComponentId, StatusVerdict Verdict, string Detail);
}

public static class StatusRealtimeEvents
{
    public const string SummaryChanged = "status.SummaryChanged";
    public const string IncidentUpdated = "status.IncidentUpdated";
}
