using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

/// <summary>
/// An account's request for a copy of its own data (GDPR Art. 15 / 20, T1-7 of
/// docs/specs/privacy.md).
///
/// <para><b>The row outlives the artifact.</b> The zip is deleted seven days after it is produced;
/// this row is not. It is the record that a subject exercised an access right, when, and until when
/// the answer was available - which is exactly what a regulator asks about and exactly what the
/// artifact's deletion would otherwise destroy.</para>
///
/// <para><b><see cref="ArtifactKey"/> is unguessable by construction.</b> It carries 256 bits from
/// the CSPRNG, so knowing an account id, an export id and the bucket name still does not let anyone
/// name the object. That is defence in depth, not the access control: the download route
/// authenticates the caller and checks ownership before it will sign anything, and the signed URL it
/// hands back is short-lived. The unguessable key is what stops a bucket misconfiguration - the
/// failure mode nobody notices until it is in the news - from turning into an enumerable index of
/// every subject's complete personal data.</para>
/// </summary>
public class DataExportRequest : BaseEntity<DataExportRequest>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "dxrq";

    public string UserId { get; set; } = null!;

    public DataExportStatus Status { get; set; } = DataExportStatus.Pending;

    /// <summary>When the subject asked. The 24-hour rate limit is measured from this, and it is
    /// deliberately distinct from <c>CreatedAt</c>, which is bookkeeping this feature must not have
    /// to trust.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>When the archive finished assembling, or when assembly failed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When the artifact stops being downloadable. Null until the archive is ready - an
    /// export that never completed has nothing to expire.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Object key in the artifact store. Null before the archive is ready and after the
    /// sweep has deleted it; see the remarks on this type for why it is random rather than derived
    /// from the ids.</summary>
    public string? ArtifactKey { get; set; }

    /// <summary>Why this request is not a complete success, when it is not. Set on
    /// <c>Failed</c> (assembly did not happen) and on <c>Partial</c> (assembly happened without some
    /// service's section). Short and non-sensitive - it is quoted back to the subject, who is
    /// entitled to know their request failed rather than to be left watching a spinner until the
    /// statutory clock runs out.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// The services whose section is absent from the archive. Empty on every status except
    /// <c>Partial</c>.
    ///
    /// <para><b>Names, not a count and not a boolean.</b> "Your export is incomplete" is not an
    /// answer a subject can act on; "the messaging service's section is missing" is - it tells them
    /// what to ask for and what not to rely on. The manifest inside the zip says the same thing, but
    /// this is the copy that survives the artifact's deletion and the copy a client can render
    /// without asking the subject to unzip anything.</para>
    ///
    /// <para>A <c>text[]</c> column, the same shape <c>UserDevice.Capabilities</c> already uses -
    /// short, fixed vocabulary, never queried by element.</para>
    /// </summary>
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

    /// <summary>
    /// Mints the object key for this request's archive.
    ///
    /// <para>Prefixed by <c>data-exports/</c> so a bucket lifecycle rule or an ops sweep can find
    /// them, and by the export id so an orphan is traceable back to a row - neither of which is what
    /// makes it unguessable. The 43-character base64url tail is.</para>
    /// </summary>
    public static string NewArtifactKey(string exportId) =>
        $"data-exports/{exportId}/{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=')}.zip";

    /// <summary>Moves a pending request to running. Idempotent, and never moves a terminal request
    /// backwards: a redelivered fan-out must not reopen an export that already failed or already
    /// expired.</summary>
    public bool BeginRunning(DateTimeOffset now)
    {
        if (Status != DataExportStatus.Pending) return false;

        Status = DataExportStatus.Running;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Publishes the finished archive as <b>complete</b>. Idempotent against a redelivered
    /// assemble command: a request that is already resolved keeps the key and the expiry it already
    /// has, so a retry cannot silently extend the artifact's life or strand the uploaded object.
    ///
    /// <para><c>Partial</c> is in the refusal set alongside <c>Ready</c>, and that is the important
    /// half: a redelivered assemble must never be able to promote an export that is missing sections
    /// into one that claims to be complete.</para></summary>
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
    /// Publishes the finished archive as <b>incomplete</b>, naming the services whose section is
    /// absent. Same key, same seven-day expiry, same download route as <see cref="MarkReady"/> - the
    /// archive is the subject's data and withholding it because the system half-failed would punish
    /// them for the system's fault. Only the claim being made about it differs.
    ///
    /// <para>An empty <paramref name="missingServices"/> is <see cref="MarkReady"/>: "partial with
    /// nothing missing" is a contradiction, and the caller deriving the list from fragment errors
    /// should not have to branch on it.</para>
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

    /// <summary>Whether this request has already reached an ending it must not be moved out of. A
    /// resolved request is one an archive was produced for (<c>Ready</c>, <c>Partial</c>) or whose
    /// archive is already gone (<c>Expired</c>); <c>Failed</c> is deliberately absent, because a
    /// failure that is later assembled successfully is a better outcome than a stuck one.</summary>
    private bool IsResolved =>
        Status is DataExportStatus.Ready or DataExportStatus.Partial or DataExportStatus.Expired;

    /// <summary>
    /// The one sentence the subject is shown about an incomplete export. Names the services rather
    /// than counting them, says the answer is not final, and says they may ask again immediately -
    /// the rate limit does not count a partial, and a subject who does not know that will wait a day
    /// they did not have to.
    /// </summary>
    private static string DescribeMissing(IReadOnlyList<string> missing)
    {
        var names = string.Join(", ", missing);
        var verb = missing.Count == 1 ? "service did not provide its section" : "services did not provide their sections";

        var reason =
            $"This export is incomplete: the {names} {verb}. Everything else is included, and "
            + "manifest.json inside the archive records each gap. You can request a new export "
            + "immediately - an incomplete export does not count towards the rate limit.";

        // The column is 512 characters. Eight service names cannot reach it today, but a truncated
        // sentence beats a write that throws on the one path that exists to tell somebody their
        // statutory disclosure is short.
        return reason.Length <= 512 ? reason : reason[..512];
    }

    /// <summary>Retires an archive whose window has closed. The key is cleared only once the object
    /// itself is gone - a row that has forgotten its key while the object survives is an artifact
    /// nothing will ever delete.</summary>
    public void MarkExpired(DateTimeOffset now)
    {
        Status = DataExportStatus.Expired;
        ArtifactKey = null;
        UpdatedAt = now;
    }

    /// <summary>Whether the artifact is past its window right now. Computed rather than stored so it
    /// cannot go stale between sweep passes - the download route refuses on this, not on
    /// <see cref="Status"/>, so a sweep that has not run yet cannot hand out an expired archive.</summary>
    public bool IsExpiredAt(DateTimeOffset now) =>
        Status == DataExportStatus.Expired || (ExpiresAt is not null && now > ExpiresAt);

    /// <summary>
    /// Whether there is an archive to hand out. <c>Partial</c> counts, and that is the point: a
    /// partial archive is still the subject's own data, produced in answer to a statutory request,
    /// and refusing to serve it would turn "some of our services were down" into "you get nothing".
    /// The status tells them what the archive is; this decides whether they can have it.
    ///
    /// <para>Expiry is deliberately not folded in - the download route checks
    /// <see cref="IsExpiredAt"/> separately so a closed window answers <c>410 Gone</c> rather than
    /// <c>409 Conflict</c>, which are different facts.</para>
    /// </summary>
    public bool IsDownloadable =>
        (Status is DataExportStatus.Ready or DataExportStatus.Partial) && ArtifactKey is not null;
}
