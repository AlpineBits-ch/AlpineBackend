using AppEnvironment;
using Identity.Application.Services.DataExport;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Commands;

/// <summary>
/// The last step of T1-7: turns the fragments <c>Echo.Sagas.ExportUserDataSaga</c> collected into
/// one zip, uploads it, and flips the <c>DataExportRequest</c> to <c>Ready</c>.
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

        // Every service that owes this archive a section and did not deliver one.
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
