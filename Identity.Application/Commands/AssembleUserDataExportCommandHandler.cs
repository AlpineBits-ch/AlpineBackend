using AppEnvironment;
using Identity.Application.Services.DataExport;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Commands;

/// <summary>
/// The last step of T1-7: turns the fragments <c>Echo.Sagas.ExportUserDataSaga</c> collected into one
/// zip, uploads it, and flips the <c>DataExportRequest</c> to <c>Ready</c>.
///
/// <para><b>In Identity rather than in the saga</b> because Identity owns the row, the bucket
/// credentials and the artifact's lifetime. The gateway process orchestrates; it does not hold
/// anybody's data. This is also the one structural difference from the deletion saga, which has no
/// assemble step at all - a purge's participants have nothing to hand back, so their acknowledgement
/// is the whole result.</para>
///
/// <para><b>Upload before the row is marked ready, and never the other way round.</b> A row that says
/// <c>Ready</c> while the object is still uploading is a 302 to a 404, delivered to somebody
/// exercising a statutory right. If the upload throws, the request is marked <c>Failed</c> with a
/// reason the subject can be shown - visibly failed beats silently pending, because a pending export
/// looks identical to one that is still working right up until the deadline passes.</para>
///
/// <para><b>Ready and Partial are decided here, from the fragments themselves.</b> A fragment
/// carrying an <c>Error</c> is a service whose section of the disclosure is absent - either because
/// it answered with a failure, or because <c>ExportUserDataSaga</c>'s deadline elapsed with it still
/// silent and wrote a stand-in in its place. Both mean the same thing to the subject, so both land
/// in <see cref="DataExportRequest.MarkPartial"/> with the service named. Deriving it here rather
/// than taking a flag from the saga is deliberate: the saga only knows about the silent ones, and a
/// service that answered "I could not produce this" leaves exactly as large a hole.</para>
///
/// <para><b>The archive is uploaded either way.</b> A partial archive is the subject's data and
/// their right to it does not depend on every one of eight services having been up; what changes is
/// only the claim the row makes about it. See <c>DataExportStatus.Partial</c>.</para>
///
/// <para>Idempotent: <see cref="DataExportRequest.MarkReady"/> and
/// <see cref="DataExportRequest.MarkPartial"/> both refuse to move an already-resolved request, so a
/// redelivered command cannot extend an artifact's life, leave the previous upload orphaned with the
/// row pointing elsewhere, or - the one that would matter most - promote a partial export into a
/// complete-looking one. No <c>SaveChangesAsync</c> - Wolverine's transactional middleware commits.</para>
/// </summary>
public class AssembleUserDataExportCommandHandler
{
    public static async Task Handle(
        AssembleUserDataExportCommand command,
        MicroserviceContext ctx,
        IDataExportArtifactStore store,
        ILogger<AssembleUserDataExportCommandHandler> logger,
        CancellationToken ct)
    {
        var request = await ctx.DataExportRequests
            .FirstOrDefaultAsync(r => r.Id == command.ExportId, ct);

        if (request is null)
        {
            logger.LogWarning("Data export {ExportId} assembled but its request row is gone", command.ExportId);
            return;
        }

        if (request.Status is Domain.Enums.DataExportStatus.Ready
            or Domain.Enums.DataExportStatus.Partial
            or Domain.Enums.DataExportStatus.Expired)
        {
            logger.LogInformation("Data export {ExportId} is already {Status}; ignoring a redelivered assemble",
                command.ExportId, request.Status);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Every service that owes this archive a section and did not deliver one. Ordinal-distinct
        // because a duplicated fragment must not name the same service twice on the row a subject
        // reads.
        var missingServices = command.Fragments
            .Where(f => f.Error is not null && !string.IsNullOrWhiteSpace(f.Service))
            .Select(f => f.Service)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        try
        {
            var archive = DataExportArchive.Build(command.ExportId, command.UserId, command.Fragments, now);
            var key = DataExportRequest.NewArtifactKey(command.ExportId);

            await store.PutAsync(key, archive.Content, ct);

            if (missingServices.Count == 0)
            {
                request.MarkReady(key, now, Env.DataExport.ArtifactTtl);

                logger.LogInformation(
                    "Data export {ExportId} ready for {UserId}: {Services} service fragment(s), {Rows} row(s), "
                    + "{Bytes} byte archive, expires {ExpiresAt:o}",
                    command.ExportId, command.UserId, command.Fragments.Count, archive.TotalRows,
                    archive.Content.Length, request.ExpiresAt);
            }
            else
            {
                request.MarkPartial(key, missingServices, now, Env.DataExport.ArtifactTtl);

                // Warning, not Information: this is a statutory disclosure that went out short, and
                // the alert the saga's deadline already raised only covers the services that went
                // silent - a service that answered with an error never trips it.
                logger.LogWarning(
                    "Data export {ExportId} is PARTIAL for {UserId}: no section from {MissingServices}. "
                    + "{Services} fragment(s), {Rows} row(s), {Bytes} byte archive, expires {ExpiresAt:o}. "
                    + "The archive is downloadable and the request is reported as incomplete; it does not "
                    + "count against the subject's rate limit, so they can ask again immediately.",
                    command.ExportId, command.UserId, string.Join(", ", missingServices),
                    command.Fragments.Count, archive.TotalRows, archive.Content.Length, request.ExpiresAt);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Data export {ExportId} failed to assemble for {UserId}",
                command.ExportId, command.UserId);

            // The message, not the stack trace or the exception type: this string is quoted back to
            // the subject through GET /api/v1/data-exports, so it must not become a channel for
            // internal detail.
            request.MarkFailed("The export could not be assembled. Please request a new one.", now);
        }
    }
}
