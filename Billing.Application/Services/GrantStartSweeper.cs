using Billing.Contracts.Bus.Events;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Billing.Application.Services;

/// <summary>Announces grants that have begun.</summary>
public class GrantStartSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<GrantStartSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Comfortably more than <see cref="Interval"/>, so a pass that was late or a process
    /// that was briefly down still covers the gap. See the class comment on why overlapping is the
    /// cheap side of the trade.</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(20);

    /// <summary>Bounded so a backlog drains over several passes instead of becoming one burst of
    /// events, matching the other sweeps in this codebase.</summary>
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Grant start sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var versions = scope.ServiceProvider.GetRequiredService<EntitlementVersionService>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var grants = scope.ServiceProvider.GetRequiredService<GrantService>();

        var announcements = await CollectAsync(
            db, versions, grants, DateTimeOffset.UtcNow, cancellationToken);

        foreach (var announcement in announcements)
        {
            await bus.PublishAsync(announcement);
        }

        if (announcements.Count > 0)
        {
            logger.LogInformation("Announced started grants for {Subjects} subject(s)", announcements.Count);
        }
    }

    /// <summary>
    /// The events this pass would publish, and the version advance that goes with each.
    /// </summary>
    internal static async Task<IReadOnlyList<EntitlementsChanged>> CollectAsync(
        MicroserviceContext db,
        EntitlementVersionService versions,
        GrantService grants,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var since = now - Lookback;

        // Three filters, and each of them is a decision.
        var started = await db.Grants
            .Where(g => g.RevokedAt == null && g.StartsAt != null
                                            && g.StartsAt > since && g.StartsAt <= now
                                            && (g.ExpiresAt == null || g.ExpiresAt > now))
            .OrderBy(g => g.StartsAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var announcements = new List<EntitlementsChanged>();

        // Read once for the whole pass rather than per grant.
        var catalogue = await grants.CatalogueAsync(cancellationToken);

        // One event per subject rather than per grant: a subject whose two grants started in the same
        // window changed once, and two events would advance the version twice for one change.
        foreach (var group in started.GroupBy(g => new EntitlementSubject(g.SubjectKind, g.SubjectId)))
        {
            var subject = group.Key;
            var newest = group.OrderByDescending(g => g.StartsAt).First();

            announcements.Add(new EntitlementsChanged
            {
                SubjectKind = subject.Kind,
                SubjectId = subject.Id,
                Reason = EntitlementsChangedReason.GrantStarted,
                GrantId = newest.Id,
                Version = await versions.AdvanceAsync(subject, cancellationToken),
                ChangedKeys = [.. group.SelectMany(grant => GrantService.KeysOf(grant, catalogue)).Distinct()],
                OccurredAt = now,
            });
        }

        return announcements;
    }
}
