using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

/// <summary>Body of <c>POST /api/v1/legal/consents</c>.</summary>
public class RecordConsentRequest
{
    public LegalDocumentType DocumentType { get; set; }

    /// <summary>
    /// The exact version being accepted, echoed back from <c>GET /api/v1/legal/documents</c>.
    ///
    /// <para>Required rather than defaulted to "whatever is current". A client that posts without a
    /// version is claiming the user accepted something the client never named, and the whole value of
    /// this record is that it says which text was agreed to.</para>
    /// </summary>
    public string Version { get; set; } = null!;
}
