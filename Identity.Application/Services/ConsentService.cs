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

/// <summary>
/// Reads and writes versioned consent records (T1-10).
///
/// <para><b>The two document types that can block are Terms and Privacy.</b> A cookie notice, where
/// one exists, is offered but never demanded - an account that has not accepted it is not in an
/// outstanding-consent state, because there is no lawful reading in which continuing to use a
/// service one has already paid for is contingent on accepting analytics storage.</para>
/// </summary>
public class ConsentService(MicroserviceContext ctx)
{
    /// <summary>The document types whose current version an account is expected to have accepted.
    /// Anything outside this set is recordable but never required.</summary>
    public static readonly LegalDocumentType[] RequiredDocumentTypes =
        [LegalDocumentType.Terms, LegalDocumentType.Privacy];

    /// <summary>
    /// The current version of each document type: the latest one whose effective date has arrived.
    ///
    /// <para>Ordered by <c>EffectiveAt</c>, never by the version string - a semver-shaped string
    /// ordered as text puts <c>1.10.0</c> before <c>1.9.0</c>, and a "current" document chosen that
    /// way would be a year out of date exactly once the versioning got interesting. Versions dated in
    /// the future are excluded so a change can be published and announced before it binds.</para>
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
    ///
    /// <para>Surfaced as the <c>consentRequired</c> array on <c>GET /users/self</c>. Empty for an
    /// account that is up to date, and empty for a deployment that has published nothing - an
    /// instance with no legal documents demands nothing, rather than blocking every client on a
    /// consent it has no document to show.</para>
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

    /// <summary>
    /// Records a consent, or returns the existing one unchanged.
    ///
    /// <para><b>Idempotent, and the first record wins.</b> A client retrying a POST whose response it
    /// never saw must not produce a second record of one decision, and it must not move the recorded
    /// timestamp or IP - those are the evidence, and the evidence is of the moment the user actually
    /// clicked, not of the last retry.</para>
    ///
    /// <para>Does <b>not</b> call SaveChanges. Callers are a mix of MVC controllers (which commit
    /// themselves) and Wolverine handlers (where the transactional middleware commits, and a manual
    /// SaveChanges is a repo-wide convention violation), so committing here would be wrong in half
    /// the call sites.</para>
    /// </summary>
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
