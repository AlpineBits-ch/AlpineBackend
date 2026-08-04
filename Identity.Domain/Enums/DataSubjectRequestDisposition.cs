namespace Identity.Domain.Enums;

/// <summary>How a data-subject request was answered.</summary>
public enum DataSubjectRequestDisposition
{
    /// <summary>The request is not closed yet.</summary>
    None,

    Fulfilled,

    /// <summary>Some of what was asked for was provided or done; the rest was refused or did not
    /// exist. The <c>Notes</c> field carries which.</summary>
    PartiallyFulfilled,

    /// <summary>Refused on a lawful ground - manifestly unfounded, excessive, or overridden by
    /// another person's rights.</summary>
    Refused,

    /// <summary>The requester withdrew it.</summary>
    Withdrawn,

    /// <summary>Identity could not be verified, or the requester is not the data subject and has no
    /// authority to act for them. Not the same as a refusal on the merits.</summary>
    NotVerified,

    /// <summary>No data is held about the named subject at all.</summary>
    NoDataHeld,
}
