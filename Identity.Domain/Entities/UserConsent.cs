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
/// </summary>
public class UserConsent : BaseEntity<UserConsent>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "ucns";

    public string UserId { get; set; } = null!;

    public LegalDocumentType DocumentType { get; set; }

    /// <summary>The <see cref="LegalDocument.Version"/> accepted.</summary>
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
