using AppEnvironment;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services.DataExport;

/// <summary>What one expiry pass did. Returned rather than only logged so a test can assert on it
/// without parsing log output - same shape as <c>RetentionSweepResult</c>.</summary>
public sealed record DataExportExpiryResult(int ArtifactsDeleted, int RowsExpired);

/// <summary>
/// The seven-day half of T1-7 (and the <c>DataExportRequest</c> artifacts row of T1-8's retention
/// table): marks archives past their window <c>Expired</c> and deletes the objects.
///
/// <para>Split from its hosted service for the same reason <c>RetentionSweep</c> is - a
/// <c>BackgroundService</c> can only be tested by starting a host and waiting, which is how deletion
/// logic ends up with no tests at all.</para>
///
/// <para><b>The row survives; only the artifact goes.</b> An export request is the record that a
/// subject exercised an access right and until when it was answered. Deleting the row along with the
/// zip would destroy the evidence that the request was ever honoured, which is the one thing a
/// regulator asks about after the fact.</para>
///
/// <para><b>The object is deleted before the row is marked.</b> A row marked <c>Expired</c> clears
/// its key (see <see cref="Domain.Entities.DataExportRequest.MarkExpired"/>), so doing it the other
/// way round would leave an archive in the bucket that nothing knows the name of any more - a copy of
/// somebody's complete personal data with no owner and no next sweep that will ever find it. Delete
/// failures are logged and swallowed by the store rather than aborting the pass: the download route
/// already refuses on <c>ExpiresAt</c>, so a stubborn key must not stop every key behind it from
/// expiring.</para>
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
