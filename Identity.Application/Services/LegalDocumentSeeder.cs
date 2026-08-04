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

        // Rows the manifest no longer declares are removed, because the manifest - not the
        // directory listing and not the table - decides what this build serves. A row left behind
        // has no file, so GET /legal/documents/{type}/{version} answers 404 for it; worse, it keeps
        // competing to be the *current* document of its type, which is decided by the latest
        // EffectiveAt. That is not hypothetical: the 0.1.0-placeholder rows were stamped with the
        // date they were seeded, which is later than the real Terms (2026-05-19) and Cookies
        // (2025-05-17) documents that replaced them, so on any database that had seen the
        // placeholders they went on outranking the real text.
        //
        // Safe against UserConsent: it stores documentType and version as plain strings with no
        // foreign key to this table (its only FK is to ApplicationUser), so a consent record
        // outlives the row it names. Removing a version somebody consented to still loses the
        // ability to serve them the text they agreed to, which is why dropping a genuinely
        // published document from the manifest must stay a deliberate act - hence the Warning.
        var undeclared = existing
            .Where(row => !declared.Any(
                d => d.DocumentType == row.DocumentType && d.Version == row.Version))
            .ToList();

        foreach (var row in undeclared)
        {
            logger.LogWarning(
                "Removing legal document {Type} v{Version}: the manifest no longer declares it, so "
                + "no file backs it and it would 404 while still competing to be the current "
                + "document of its type",
                row.DocumentType, row.Version);
        }

        if (undeclared.Count > 0)
        {
            ctx.LegalDocuments.RemoveRange(undeclared);
            changed = true;
        }

        if (changed) await ctx.SaveChangesAsync(ct);
    }
}
