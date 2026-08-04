using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Request;

/// <summary>Body of <c>POST /api/v1/admin/dsr</c> - intake of a rights request that arrived out of
/// band.</summary>
public class OpenDataSubjectRequest
{
    /// <summary>Address the request came from or concerns. The only mandatory identifier: most of
    /// what this queue tracks comes from people with no account here.</summary>
    public string SubjectEmail { get; set; } = null!;

    public DataSubjectRequestType Type { get; set; }

    /// <summary>What was asked for, in the requester's words. Becomes the first line of the working
    /// notes.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When the request actually arrived, if that is not now.
    ///
    /// <para>Present because the statutory clock runs from receipt, not from data entry: a letter
    /// opened on Friday may have arrived on Monday, and back-dating it is the honest thing to do even
    /// though it shortens the remaining window. Future dates are refused.</para>
    /// </summary>
    public DateTimeOffset? ReceivedAt { get; set; }
}

/// <summary>
/// Body of <c>PATCH /api/v1/admin/dsr/{id}</c>. Every field optional; omitted means "leave alone",
/// matching the PATCH semantics of the privacy-settings endpoint.
/// </summary>
public class UpdateDataSubjectRequest
{
    public DataSubjectRequestStatus? Status { get; set; }

    /// <summary>Required when moving to <c>Closed</c>, and must not be <c>None</c> - closing without
    /// saying how it was answered is the same as not answering.</summary>
    public DataSubjectRequestDisposition? Disposition { get; set; }

    /// <summary>Staff account taking the request. Pass an empty string to unassign.</summary>
    public string? AssignedToStaffUserId { get; set; }

    /// <summary>A line to append to the working notes. Never replaces what is there.</summary>
    public string? Note { get; set; }

    /// <summary>The account matched to this subject, once one has been identified.</summary>
    public string? SubjectUserId { get; set; }
}
