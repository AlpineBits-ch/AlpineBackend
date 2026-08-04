using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateUserConsentParams
{
    public string UserId { get; init; } = null!;
    public LegalDocumentType DocumentType { get; init; }
    public string Version { get; init; } = null!;
    public string? IpAddress { get; init; }
    public DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>
/// The record that a specific account accepted a specific version of a specific document, at a
/// specific time, from a specific address (T1-10).
///
/// <para><b>Append-only, one row per (user, type, version).</b> Publishing a new version does not
/// touch the old rows - it adds a state where the account owes a consent it has not given yet, which
/// the client is told about through the <c>consentRequired</c> array on <c>GET /users/self</c>.
/// Updating the old row to point at the new version would destroy the only evidence of what the user
/// was actually shown, which is the one thing this table is for.</para>
///
/// <para><b>The IP is part of the evidence, and is deliberately exempt from the T1-8 audit-IP
/// scrub.</b> A consent record without an origin is materially weaker as proof, and unlike a login
/// IP it is not incidental telemetry - it is the record. It goes when the account goes, via the
/// tombstone/purge path.</para>
/// </summary>
public class UserConsent : BaseEntity<UserConsent>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "ucns";

    public string UserId { get; set; } = null!;

    public LegalDocumentType DocumentType { get; set; }

    /// <summary>The <see cref="LegalDocument.Version"/> accepted. Stored as the string rather than
    /// as an FK to the document row so a consent survives a document row being re-seeded.</summary>
    public string Version { get; set; } = null!;

    public DateTimeOffset AcceptedAt { get; set; }

    public string? IpAddress { get; set; }

    public static UserConsent Create(CreateUserConsentParams parameters)
    {
        var acceptedAt = parameters.AcceptedAt == default ? DateTimeOffset.UtcNow : parameters.AcceptedAt;
        return new UserConsent
        {
            Id = GenerateId(),
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            UserId = parameters.UserId,
            DocumentType = parameters.DocumentType,
            Version = parameters.Version,
            AcceptedAt = acceptedAt,
            IpAddress = parameters.IpAddress,
        };
    }
}
