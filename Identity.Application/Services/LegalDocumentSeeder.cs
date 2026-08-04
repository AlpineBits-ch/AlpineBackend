using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>
/// Reconciles the <c>legal_documents</c> table with what this build actually ships (T1-12).
///
/// <para>Runs once at startup rather than from an endpoint: publishing a legal document is a deploy,
/// reviewed like any other change, not a runtime write some session can make. There is deliberately
/// no "create legal document" route anywhere in this service.</para>
///
/// <para><b>A content-hash mismatch is logged as an error, loudly, and then the stored hash is
/// updated.</b> Refusing to start would take the whole service down over a typo fix in a placeholder;
/// silently ignoring it would defeat the only reason the hash is stored. Recording it and moving on
/// means the drift is in the log with both hashes in it, which is what an auditor needs and what a
/// crash loop would not have given them.</para>
/// </summary>
public class LegalDocumentSeeder(
    IServiceScopeFactory scopeFactory,
    LegalDocumentCatalog catalog,
    ILogger<LegalDocumentSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Same reasoning as the mismatch case: a seeding failure must not stop the service from
            // serving authentication. An empty legal_documents table means nothing is demanded of
            // any account, which is a degraded state, not a dangerous one.
            logger.LogError(ex, "Seeding legal documents failed; the service will start without them");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task SeedAsync(CancellationToken ct)
    {
        var declared = catalog.Load();
        if (declared.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var existing = await ctx.LegalDocuments.ToListAsync(ct);
        var changed = false;

        foreach (var file in declared)
        {
            var row = existing.FirstOrDefault(
                d => d.DocumentType == file.DocumentType && d.Version == file.Version);

            if (row is null)
            {
                ctx.LegalDocuments.Add(LegalDocument.Create(new CreateLegalDocumentParams
                {
                    DocumentType = file.DocumentType,
                    Version = file.Version,
                    EffectiveAt = file.EffectiveAt,
                    ContentHash = file.ContentHash,
                    Url = catalog.UrlFor(file.DocumentType, file.Version),
                }));
                changed = true;
                logger.LogInformation("Published legal document {Type} v{Version} ({Hash})",
                    file.DocumentType, file.Version, file.ContentHash);
                continue;
            }

            if (row.ContentHash != file.ContentHash)
            {
                logger.LogError(
                    "Legal document {Type} v{Version} changed after publication: stored hash {Stored}, "
                    + "file hash {Actual}. Every consent recorded against this version was given "
                    + "against the OLD content. A change to a published document must be a new "
                    + "version, not an edit.",
                    file.DocumentType, file.Version, row.ContentHash, file.ContentHash);

                row.ContentHash = file.ContentHash;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (row.EffectiveAt != file.EffectiveAt)
            {
                row.EffectiveAt = file.EffectiveAt;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            var url = catalog.UrlFor(file.DocumentType, file.Version);
            if (row.Url != url)
            {
                // The public hostname can legitimately move; the content cannot. Updated quietly.
                row.Url = url;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        if (changed) await ctx.SaveChangesAsync(ct);
    }
}
