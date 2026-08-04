using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>One document a caller still owes a consent for.</summary>
public sealed record OutstandingConsent(
    LegalDocumentType DocumentType,
    string Version,
    DateTimeOffset EffectiveAt,
    string Url);

/// <summary>Reads and writes versioned consent records (T1-10).</summary>
public class ConsentService(MicroserviceContext ctx)
{
    /// <summary>The document types whose current version an account is expected to have accepted.
    /// Anything outside this set is recordable but never required.</summary>
    public static readonly LegalDocumentType[] RequiredDocumentTypes =
        [LegalDocumentType.Terms, LegalDocumentType.Privacy];

    /// <summary>
    /// The current version of each document type: the latest one whose effective date has arrived.
    /// </summary>
    public async Task<IReadOnlyList<LegalDocument>> GetCurrentDocumentsAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        var published = await ctx.LegalDocuments.AsNoTracking()
            .Where(d => d.EffectiveAt <= now)
            .ToListAsync(ct);

        return published
            .GroupBy(d => d.DocumentType)
            .Select(g => g.OrderByDescending(d => d.EffectiveAt).ThenByDescending(d => d.CreatedAt).First())
            .OrderBy(d => d.DocumentType)
            .ToList();
    }

    public Task<List<UserConsent>> GetConsentsAsync(string userId, CancellationToken ct = default) =>
        ctx.UserConsents.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.AcceptedAt)
            .ToListAsync(ct);

    /// <summary>
    /// The required documents whose current version this account has not accepted.
    /// </summary>
    public async Task<IReadOnlyList<OutstandingConsent>> GetOutstandingAsync(
        string userId, DateTimeOffset now, CancellationToken ct = default)
    {
        var current = (await GetCurrentDocumentsAsync(now, ct))
            .Where(d => RequiredDocumentTypes.Contains(d.DocumentType))
            .ToList();

        if (current.Count == 0) return [];

        var accepted = await ctx.UserConsents.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.DocumentType, c.Version })
            .ToListAsync(ct);

        return current
            .Where(d => !accepted.Any(a => a.DocumentType == d.DocumentType && a.Version == d.Version))
            .Select(d => new OutstandingConsent(d.DocumentType, d.Version, d.EffectiveAt, d.Url))
            .ToList();
    }

    /// <summary>Records a consent, or returns the existing one unchanged.</summary>
    public async Task<UserConsent> RecordAsync(
        string userId,
        LegalDocumentType documentType,
        string version,
        string? ipAddress,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default)
    {
        var existing = await ctx.UserConsents.FirstOrDefaultAsync(
            c => c.UserId == userId && c.DocumentType == documentType && c.Version == version, ct);

        if (existing is not null) return existing;

        var consent = UserConsent.Create(new CreateUserConsentParams
        {
            UserId = userId,
            DocumentType = documentType,
            Version = version,
            IpAddress = ipAddress,
            AcceptedAt = acceptedAt,
        });

        ctx.UserConsents.Add(consent);
        return consent;
    }
}
