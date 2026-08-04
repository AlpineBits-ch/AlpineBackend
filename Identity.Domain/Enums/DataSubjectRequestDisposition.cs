namespace Identity.Domain.Enums;

/// <summary>
/// How a data-subject request was answered.
///
/// <para>A refusal is a first-class outcome rather than an absence of one. "We refused, and here is
/// why" is a lawful answer; "we never recorded what we did" is not, and an unanswered request is the
/// violation regardless of what the answer would have been.</para>
/// </summary>
public enum DataSubjectRequestDisposition
{
    /// <summary>The request is not closed yet. The only valid value while
    /// <see cref="DataSubjectRequestStatus.Open"/> or <see cref="DataSubjectRequestStatus.InProgress"/>.</summary>
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
