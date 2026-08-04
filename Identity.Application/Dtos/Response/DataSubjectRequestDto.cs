using Identity.Domain.Entities;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>A queue entry as rendered to staff (T1-13).</summary>
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

    /// <summary>Whole days left before the deadline; negative once past it.</summary>
    public int DaysRemaining { get; set; }

    /// <summary>True when the request was answered after its deadline.</summary>
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
