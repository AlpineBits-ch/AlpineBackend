using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

/// <summary>An account's request for a copy of its own data (GDPR Art.</summary>
public class DataExportRequest : BaseEntity<DataExportRequest>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "dxrq";

    public string UserId { get; set; } = null!;

    public DataExportStatus Status { get; set; } = DataExportStatus.Pending;

    /// <summary>When the subject asked.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>When the archive finished assembling, or when assembly failed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When the artifact stops being downloadable.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Object key in the artifact store.</summary>
    public string? ArtifactKey { get; set; }

    /// <summary>Why this request is not a complete success, when it is not.</summary>
    public string? FailureReason { get; set; }

    /// <summary>The services whose section is absent from the archive.</summary>
    public List<string> MissingServices { get; set; } = [];

    public static DataExportRequest Create(string userId, DateTimeOffset now)
    {
        return new DataExportRequest
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            UserId = userId,
            Status = DataExportStatus.Pending,
            RequestedAt = now,
        };
    }

    /// <summary>Mints the object key for this request's archive.</summary>
    public static string NewArtifactKey(string exportId) =>
        $"data-exports/{exportId}/{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=')}.zip";

    /// <summary>Moves a pending request to running.</summary>
    public bool BeginRunning(DateTimeOffset now)
    {
        if (Status != DataExportStatus.Pending) return false;

        Status = DataExportStatus.Running;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Publishes the finished archive as complete.</summary>
    public bool MarkReady(string artifactKey, DateTimeOffset now, TimeSpan ttl)
    {
        if (IsResolved) return false;

        Status = DataExportStatus.Ready;
        ArtifactKey = artifactKey;
        CompletedAt = now;
        ExpiresAt = now.Add(ttl);
        FailureReason = null;
        MissingServices = [];
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Publishes the finished archive as incomplete, naming the services whose section is absent.
    /// </summary>
    public bool MarkPartial(
        string artifactKey, IEnumerable<string> missingServices, DateTimeOffset now, TimeSpan ttl)
    {
        var missing = missingServices
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0) return MarkReady(artifactKey, now, ttl);
        if (IsResolved) return false;

        Status = DataExportStatus.Partial;
        ArtifactKey = artifactKey;
        CompletedAt = now;
        ExpiresAt = now.Add(ttl);
        MissingServices = missing;
        FailureReason = DescribeMissing(missing);
        UpdatedAt = now;
        return true;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        if (IsResolved) return;

        Status = DataExportStatus.Failed;
        CompletedAt = now;
        FailureReason = reason;
        UpdatedAt = now;
    }

    /// <summary>
    /// Whether this request has already reached an ending it must not be moved out of.
    /// </summary>
    private bool IsResolved =>
        Status is DataExportStatus.Ready or DataExportStatus.Partial or DataExportStatus.Expired;

    /// <summary>The one sentence the subject is shown about an incomplete export.</summary>
    private static string DescribeMissing(IReadOnlyList<string> missing)
    {
        var names = string.Join(", ", missing);
        var verb = missing.Count == 1 ? "service did not provide its section" : "services did not provide their sections";

        var reason =
            $"This export is incomplete: the {names} {verb}. Everything else is included, and "
            + "manifest.json inside the archive records each gap. You can request a new export "
            + "immediately - an incomplete export does not count towards the rate limit.";

        // The column is 512 characters.
        return reason.Length <= 512 ? reason : reason[..512];
    }

    /// <summary>Retires an archive whose window has closed.</summary>
    public void MarkExpired(DateTimeOffset now)
    {
        Status = DataExportStatus.Expired;
        ArtifactKey = null;
        UpdatedAt = now;
    }

    /// <summary>Whether the artifact is past its window right now.</summary>
    public bool IsExpiredAt(DateTimeOffset now) =>
        Status == DataExportStatus.Expired || (ExpiresAt is not null && now > ExpiresAt);

    /// <summary>Whether there is an archive to hand out.</summary>
    public bool IsDownloadable =>
        (Status is DataExportStatus.Ready or DataExportStatus.Partial) && ArtifactKey is not null;
}
