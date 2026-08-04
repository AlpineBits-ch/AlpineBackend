using AppEnvironment;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services.DataExport;

/// <summary>What one expiry pass did.</summary>
public sealed record DataExportExpiryResult(int ArtifactsDeleted, int RowsExpired);

/// <summary>
/// The seven-day half of T1-7 (and the <c>DataExportRequest</c> artifacts row of T1-8's retention
/// table): marks archives past their window <c>Expired</c> and deletes the objects.
/// </summary>
public static class DataExportExpirySweep
{
    public static async Task<DataExportExpiryResult> RunAsync(
        MicroserviceContext ctx,
        IDataExportArtifactStore store,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Ready OR Partial. A partial archive is a real object in the bucket holding real personal
        // data - it is short a section, not empty - so leaving it out of this query would mean every
        // export that came back incomplete lives in the bucket forever, unswept and undeletable once
        // its row has forgotten nothing. Written as two comparisons rather than a Contains so the
        // string-converted enum translates cleanly on both the Npgsql and the InMemory provider.
        var due = await ctx.DataExportRequests
            .Where(r => (r.Status == DataExportStatus.Ready || r.Status == DataExportStatus.Partial)
                        && r.ExpiresAt != null && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt)
            .Take(Env.DataExport.SweepBatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return new DataExportExpiryResult(0, 0);

        var deleted = 0;

        foreach (var request in due)
        {
            if (request.ArtifactKey is { } key)
            {
                await store.DeleteAsync(key, ct);
                deleted++;
            }

            request.MarkExpired(now);
        }

        await ctx.SaveChangesAsync(ct);

        return new DataExportExpiryResult(deleted, due.Count);
    }
}
