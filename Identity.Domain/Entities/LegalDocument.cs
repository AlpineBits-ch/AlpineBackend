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

/// <summary>
/// One published version of one legal document (T1-10 / T1-12).
///
/// <para><b>A row is never edited once published, only superseded.</b> A consent record points at
/// <c>(DocumentType, Version)</c>, so rewriting a row's text - or its <see cref="ContentHash"/>, or
/// its <see cref="EffectiveAt"/> - would silently change what every stored consent claims the user
/// agreed to. That is the failure this table exists to make impossible: the hash is recomputed from
/// the served bytes on every startup, so an edit in place is detectable rather than invisible.</para>
///
/// <para><b>Rows are seeded from the repository, not written by an endpoint.</b> There is no
/// "publish a legal document" API and there should not be one - publication is a deploy, reviewed
/// like any other change, not a runtime write some session can make.</para>
/// </summary>
public class LegalDocument : BaseEntity<LegalDocument>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "lgdc";

    public LegalDocumentType DocumentType { get; set; }

    /// <summary>Opaque version string, unique per <see cref="DocumentType"/>. Not parsed and not
    /// ordered - <see cref="EffectiveAt"/> is what decides which version is current, because a
    /// semver-looking string ordered as a string puts <c>1.10.0</c> before <c>1.9.0</c>.</summary>
    public string Version { get; set; } = null!;

    /// <summary>When this version took (or takes) effect. A version dated in the future is
    /// published but not yet current, which is how a change can be announced before it binds.</summary>
    public DateTimeOffset EffectiveAt { get; set; }

    /// <summary>Lowercase hex SHA-256 of the served bytes. The whole point of storing it is that a
    /// later mismatch is evidence.</summary>
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
