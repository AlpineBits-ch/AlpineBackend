namespace Identity.Domain.Enums;

/// <summary>
/// Lifecycle of a <see cref="Entities.DataExportRequest"/> (T1-7).
///
/// <para>Persisted as a <b>string</b>, not as a Postgres enum type - same call as
/// <c>LegalDocumentType</c> and the DSR enums, and for the same reason: a Postgres enum is a
/// database-wide object whose creation has to be repeated in every migration's annotation block and
/// in every hand-built <c>DbContextOptions</c>, and this feature is not worth that coupling.</para>
/// </summary>
public enum DataExportStatus
{
    /// <summary>Row committed, fan-out published, nobody has answered yet.</summary>
    Pending,

    /// <summary>At least one participant has started producing its fragment.</summary>
    Running,

    /// <summary>Archive assembled and uploaded; <c>ArtifactKey</c> and <c>ExpiresAt</c> are set.</summary>
    Ready,

    /// <summary>Assembly failed. Terminal - the subject requests again rather than the system
    /// retrying silently, so a failure is visible to the person waiting on a statutory deadline.</summary>
    Failed,

    /// <summary>Past <c>ExpiresAt</c>; the artifact has been deleted. The row is kept, because "you
    /// asked for an export on the 3rd and it was available until the 10th" is itself the record of
    /// how the request was answered.</summary>
    Expired,

    /// <summary>
    /// Archive assembled, uploaded and downloadable - but at least one participating service's
    /// section is missing, and <c>DataExportRequest.MissingServices</c> names which.
    ///
    /// <para><b>Why this is not <c>Ready</c>.</b> Under Art. 15 "here is everything we hold about
    /// you" and "here is most of it" are materially different answers, and <c>Status</c> is the
    /// field a subject - or a client rendering one line per request - reads to tell them apart. An
    /// export missing a whole service's worth of data that reports itself complete is a false
    /// statement about a statutory disclosure, and the manifest inside the zip disclosing the gap
    /// does not repair it: nobody unzips an archive that says it worked.</para>
    ///
    /// <para><b>Why this is not <c>Failed</c>.</b> The archive exists and it is the subject's data.
    /// They are entitled to it, so the download route serves a <c>Partial</c> exactly as it serves a
    /// <c>Ready</c> - see <c>DataExportRequest.IsDownloadable</c>. Terminal in the same sense
    /// <c>Ready</c> is: it expires on the same seven-day clock, and it does not top itself up if the
    /// absent service comes back.</para>
    ///
    /// <para><b>Declared last on purpose.</b> The column is a string so the ordinal never reaches
    /// the database, but appending rather than inserting keeps every existing member's numeric value
    /// unchanged for anything that does serialize this as an integer.</para>
    /// </summary>
    Partial,
}
