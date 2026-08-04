using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

/// <summary>Body of <c>POST /api/v1/legal/consents</c>.</summary>
public class RecordConsentRequest
{
    public LegalDocumentType DocumentType { get; set; }

    /// <summary>
    /// The exact version being accepted, echoed back from <c>GET /api/v1/legal/documents</c>.
    /// </summary>
    public string Version { get; set; } = null!;
}
