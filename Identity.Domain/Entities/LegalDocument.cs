using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateLegalDocumentParams
{
    public LegalDocumentType DocumentType { get; init; }
    public string Version { get; init; } = null!;
    public DateTimeOffset EffectiveAt { get; init; }
    public string ContentHash { get; init; } = null!;
    public string Url { get; init; } = null!;
}

/// <summary>One published version of one legal document (T1-10 / T1-12).</summary>
public class LegalDocument : BaseEntity<LegalDocument>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "lgdc";

    public LegalDocumentType DocumentType { get; set; }

    /// <summary>Opaque version string, unique per <see cref="DocumentType"/>.</summary>
    public string Version { get; set; } = null!;

    /// <summary>When this version took (or takes) effect.</summary>
    public DateTimeOffset EffectiveAt { get; set; }

    /// <summary>Lowercase hex SHA-256 of the served bytes.</summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>Publicly fetchable address of exactly these bytes.</summary>
    public string Url { get; set; } = null!;

    public static LegalDocument Create(CreateLegalDocumentParams parameters)
    {
        var now = DateTimeOffset.UtcNow;
        return new LegalDocument
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            DocumentType = parameters.DocumentType,
            Version = parameters.Version,
            EffectiveAt = parameters.EffectiveAt,
            ContentHash = parameters.ContentHash,
            Url = parameters.Url,
        };
    }
}
