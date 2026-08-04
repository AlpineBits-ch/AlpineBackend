using Identity.Domain.Entities;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>
/// A queue entry as rendered to staff (T1-13).
///
/// <para><see cref="IsOverdue"/> and <see cref="DaysRemaining"/> are computed at read time from
/// <c>DueAt</c> rather than stored, so a request cannot be quietly not-overdue because nobody ran a
/// job. An unanswered request is the violation - the queue has to make that visible without anything
/// having to notice on a schedule.</para>
/// </summary>
public class DataSubjectRequestDto
{
    public string Id { get; set; } = null!;
    public string SubjectEmail { get; set; } = null!;
    public string? SubjectUserId { get; set; }
    public DataSubjectRequestType Type { get; set; }
    public DataSubjectRequestStatus Status { get; set; }
    public DataSubjectRequestDisposition Disposition { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string OpenedByStaffUserId { get; set; } = null!;
    public string? AssignedToStaffUserId { get; set; }
    public string? ClosedByStaffUserId { get; set; }

    public bool IsOverdue { get; set; }

    /// <summary>Whole days left before the deadline; negative once past it. Negative rather than
    /// clamped to zero so "three days late" and "due today" are different numbers on the queue.</summary>
    public int DaysRemaining { get; set; }

    /// <summary>True when the request was answered after its deadline. Kept distinct from
    /// <see cref="IsOverdue"/>, which is only ever true while a request is still open - a closed-late
    /// request is a reportable fact, not outstanding work.</summary>
    public bool ClosedLate { get; set; }

    public static DataSubjectRequestDto From(DataSubjectRequest request, DateTimeOffset now) => new()
    {
        Id = request.Id,
        SubjectEmail = request.SubjectEmail,
        SubjectUserId = request.SubjectUserId,
        Type = request.Type,
        Status = request.Status,
        Disposition = request.Disposition,
        Notes = request.Notes,
        ReceivedAt = request.ReceivedAt,
        DueAt = request.DueAt,
        ClosedAt = request.ClosedAt,
        OpenedByStaffUserId = request.OpenedByStaffUserId,
        AssignedToStaffUserId = request.AssignedToStaffUserId,
        ClosedByStaffUserId = request.ClosedByStaffUserId,
        IsOverdue = request.IsOverdueAt(now),
        DaysRemaining = (int)Math.Floor((request.DueAt - now).TotalDays),
        ClosedLate = request.ClosedAt is not null && request.ClosedAt > request.DueAt,
    };
}
