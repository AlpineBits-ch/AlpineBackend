using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Billing.Application.Credit;

/// <summary>
/// Retires lots that have run out of time, and warns about the ones that are close.
/// </summary>
public class CreditExpirySweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<CreditExpirySweeper> logger) : BackgroundService
{
    /// <summary>Slower than the grant sweep's five minutes, on purpose.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Bounded so a backlog drains over several passes rather than becoming one burst of
    /// writes and notifications, matching the other sweeps in this codebase.</summary>
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
                logger.LogError(exception, "Credit expiry sweep failed");
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
        var ledger = scope.ServiceProvider.GetRequiredService<CreditLedgerService>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var expired = await ledger.ExpireLotsAsync(BatchSize, cancellationToken);

        if (expired.Count > 0)
        {
            logger.LogInformation(
                "Expired {Points} credit(s) across {Lots} lot(s)",
                -expired.Sum(entry => entry.Amount), expired.Count);
        }

        // Warnings after expiries, so a lot that lapsed in the same pass is never warned about on its
        // way out. The window makes that nearly impossible anyway; the ordering makes it impossible.
        foreach (var warning in await CollectWarningsAsync(ledger, db, BatchSize, cancellationToken))
        {
            await bus.PublishAsync(warning);
        }
    }

    /// <summary>Claims the lots that are due a warning and turns them into events.</summary>
    internal static async Task<IReadOnlyList<CreditExpiringSoon>> CollectWarningsAsync(
        CreditLedgerService ledger,
        MicroserviceContext db,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(db);

        var claimed = await ledger.ClaimExpiryWarningsAsync(batchSize, cancellationToken);
        if (claimed.Count == 0) return [];

        var lotIds = claimed.Select(lot => lot.Id).ToList();

        // Re-read the remainders after the claim rather than reusing what the claim query saw.
        var remaining = await db.CreditEntries
            .Where(entry => entry.LotId != null && lotIds.Contains(entry.LotId))
            .GroupBy(entry => entry.LotId!)
            .Select(group => new { LotId = group.Key, Points = group.Sum(entry => entry.Amount) })
            .ToListAsync(cancellationToken);

        var byLot = remaining.ToDictionary(row => row.LotId, row => row.Points);
        var now = DateTimeOffset.UtcNow;

        return claimed
            .Where(lot => byLot.TryGetValue(lot.Id, out var points) && points > 0)
            .Select(lot => new CreditExpiringSoon
            {
                UserId = lot.UserId,
                LotId = lot.Id,
                Points = byLot[lot.Id],
                ExpiresAt = lot.ExpiresAt,
                CampaignId = lot.CampaignId,
                OccurredAt = now,
            })
            .ToList();
    }
}
