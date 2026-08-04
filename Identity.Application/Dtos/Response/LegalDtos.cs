using Identity.Application.Services;
using Identity.Domain.Entities;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>
/// A published legal document version, as served by <c>GET /api/v1/legal/documents</c>.
/// </summary>
public class LegalDocumentDto
{
    public LegalDocumentType DocumentType { get; set; }
    public string Version { get; set; } = null!;
    public DateTimeOffset EffectiveAt { get; set; }
    public string ContentHash { get; set; } = null!;
    public string Url { get; set; } = null!;

    /// <summary>Whether an account is expected to have accepted this document's current version -
    /// true for Terms and Privacy, false for anything optional. Published so a client does not have
    /// to hard-code the list.</summary>
    public bool ConsentRequired { get; set; }

    public static LegalDocumentDto From(LegalDocument document) => new()
    {
        DocumentType = document.DocumentType,
        Version = document.Version,
        EffectiveAt = document.EffectiveAt,
        ContentHash = document.ContentHash,
        Url = document.Url,
        ConsentRequired = ConsentService.RequiredDocumentTypes.Contains(document.DocumentType),
    };
}

/// <summary>One recorded acceptance, as served by <c>GET /api/v1/legal/consents</c>.</summary>
public class UserConsentDto
{
    public LegalDocumentType DocumentType { get; set; }
    public string Version { get; set; } = null!;
    public DateTimeOffset AcceptedAt { get; set; }

    public static UserConsentDto From(UserConsent consent) => new()
    {
        DocumentType = consent.DocumentType,
        Version = consent.Version,
        AcceptedAt = consent.AcceptedAt,
    };
}

/// <summary>
/// One document the account still owes a consent for - the shape of the <c>consentRequired</c>
/// array on <c>GET /api/v1/users/self</c> (T1-10).
/// </summary>
public class OutstandingConsentDto
{
    public LegalDocumentType DocumentType { get; set; }
    public string Version { get; set; } = null!;
    public DateTimeOffset EffectiveAt { get; set; }
    public string Url { get; set; } = null!;

    public static OutstandingConsentDto From(OutstandingConsent outstanding) => new()
    {
        DocumentType = outstanding.DocumentType,
        Version = outstanding.Version,
        EffectiveAt = outstanding.EffectiveAt,
        Url = outstanding.Url,
    };
}
