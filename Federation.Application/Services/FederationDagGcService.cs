using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Federation.Application.Services;

/// <summary>
/// Bounds how long an unresolved inbound DAG buffer entry (FederatedEventRecord with
/// Applied=false) can sit waiting for a parent that never arrives - a permanently defederated
/// sender, an event lost before this fix's delivery-retry existed, or any other permanent gap.
/// Without this, those rows accumulate forever with no way to ever apply them.
///
/// This is a deliberate availability-over-consistency tradeoff: dropping a stuck buffered event
/// means that branch of the scope's history is permanently lost here, rather than blocking
/// forever on a parent that isn't coming. See the split-brain section of the federation protocol
/// doc.
/// </summary>
public class FederationDagGcService(
    IServiceScopeFactory scopeFactory,
    ILogger<FederationDagGcService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxBufferAge = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(SweepInterval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

                var cutoff = DateTimeOffset.UtcNow - MaxBufferAge;
                var stuck = await db.FederatedEvents
                    .Where(e => !e.Applied && e.ReceivedAt < cutoff)
                    .ToListAsync(stoppingToken);

                if (stuck.Count == 0) continue;

                logger.LogWarning(
                    "Dropping {Count} inbound federation events stuck unapplied for over {MaxAge} - " +
                    "their parent event never arrived. Scopes affected: {Scopes}",
                    stuck.Count, MaxBufferAge, string.Join(", ", stuck.Select(e => e.ScopeKey).Distinct()));

                db.FederatedEvents.RemoveRange(stuck);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Federation DAG buffer GC sweep failed");
            }
        }
    }
}
