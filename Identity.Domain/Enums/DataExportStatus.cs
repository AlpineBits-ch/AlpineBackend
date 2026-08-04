namespace Identity.Domain.Enums;

/// <summary>Lifecycle of a <see cref="Entities.DataExportRequest"/> (T1-7).</summary>
public enum DataExportStatus
{
    /// <summary>Row committed, fan-out published, nobody has answered yet.</summary>
    Pending,

    /// <summary>At least one participant has started producing its fragment.</summary>
    Running,

    /// <summary>Archive assembled and uploaded; <c>ArtifactKey</c> and <c>ExpiresAt</c> are set.</summary>
    Ready,

    /// <summary>Assembly failed.</summary>
    Failed,

    /// <summary>Past <c>ExpiresAt</c>; the artifact has been deleted.</summary>
    Expired,

    /// <summary>
    /// Archive assembled, uploaded and downloadable - but at least one participating service's
    /// section is missing, and <c>DataExportRequest.MissingServices</c> names which.
    /// </summary>
    Partial,
}
