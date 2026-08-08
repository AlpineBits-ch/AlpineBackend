using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The second chance: asks the live source about barcodes a scan could not resolve inline, a
/// handful at a time, on the household sweep.
/// </summary>
public class ProductCatalogFillService(
    MicroserviceContext ctx, ProductCatalogLookupService lookups,
    ILogger<ProductCatalogFillService> logger)
{
    /// <summary>Asks about the misses that are due, and returns how many were resolved.</summary>
    public async Task<int> FillAsync(CancellationToken ct = default)
    {
        var config = Env.ProductCatalog;

        // Checked here as well as inside the lookup service, which is otherwise the only gate that
        // matters: this one exists to keep a switched-off instance from running a database query
        // every five minutes to build a batch it is never allowed to ask about.
        if (!config.LiveFillEnabled) return 0;

        var now = DateTimeOffset.UtcNow;

        var due = await ctx.ProductCatalogMisses
            .Where(m => m.RetryAfter != null && m.RetryAfter <= now)
            // Longest-waiting first, so a backlog drains in the order it accumulated rather than in
            // whatever order the planner returns.
            .OrderBy(m => m.RetryAfter)
            .Take(Math.Max(1, config.FillBatchSize))
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        var resolved = 0;
        var asked = 0;

        foreach (var miss in due)
        {
            if (ct.IsCancellationRequested) break;

            var outcome = await lookups.LookupAsync(miss.Barcode, config.RequestTimeout, BackfillReserve, ct);

            // Nothing was asked - no budget, or the feature is gated off between the check above
            // and here.
            if (outcome.Kind == ProductCatalogLookupService.LookupKind.NotAttempted) break;

            asked++;

            switch (outcome.Kind)
            {
                case ProductCatalogLookupService.LookupKind.Found:
                    if (ProductCatalogLookupService.BuildEntry(
                            miss.Barcode, outcome.Product!, now) is { } entry)
                    {
                        ctx.ProductCatalogEntries.Add(entry);
                        ctx.ProductCatalogMisses.Remove(miss);
                        resolved++;
                    }
                    else
                    {
                        // The source has it and cannot name it.
                        miss.RecordAbsent(now);
                    }

                    break;

                case ProductCatalogLookupService.LookupKind.Absent:
                    miss.RecordAbsent(now);
                    break;

                default:
                    miss.RecordUnreachable(now);
                    break;
            }
        }

        await ctx.SaveChangesAsync(ct);

        if (resolved > 0)
            logger.LogDebug("Product catalog filled {Resolved} of {Asked} due misses", resolved, asked);

        return resolved;
    }

    /// <summary>
    /// How many tokens the sweep refuses to take the instance below, so that a scan arriving
    /// mid-backfill still finds budget.
    /// </summary>
    private static int BackfillReserve => Math.Max(1, Env.ProductCatalog.BurstCapacity / 2);
}
