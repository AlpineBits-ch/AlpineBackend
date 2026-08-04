using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateDataSubjectRequestParams
{
    public string SubjectEmail { get; init; } = null!;
    public DataSubjectRequestType Type { get; init; }
    public string? Notes { get; init; }
    public string OpenedByStaffUserId { get; init; } = null!;

    /// <summary>When the request was actually received, which is not necessarily when it was typed
    /// into the queue - a letter opened on Friday may have arrived on Monday, and the clock runs
    /// from arrival. Defaults to now when unset.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The statutory response window.</summary>
    public TimeSpan ResponseWindow { get; init; }
}

/// <summary>
/// A rights request that arrived out of band - by email, by post, or from someone who has no
/// account here at all (T1-13).
/// </summary>
public class DataSubjectRequest : BaseEntity<DataSubjectRequest>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "dsrq";

    /// <summary>Address the request came from / concerns.</summary>
    public string SubjectEmail { get; set; } = null!;

    /// <summary>The account this was matched to, when one could be matched.</summary>
    public string? SubjectUserId { get; set; }

    public DataSubjectRequestType Type { get; set; }

    public DataSubjectRequestStatus Status { get; set; } = DataSubjectRequestStatus.Open;

    public DataSubjectRequestDisposition Disposition { get; set; } = DataSubjectRequestDisposition.None;

    /// <summary>Free-text working notes.</summary>
    public string? Notes { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Statutory deadline, stamped once at intake.</summary>
    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The staff account that opened the request.</summary>
    public string OpenedByStaffUserId { get; set; } = null!;

    public string? AssignedToStaffUserId { get; set; }

    public string? ClosedByStaffUserId { get; set; }

    /// <summary>Past its deadline and still unanswered.</summary>
    public bool IsOverdueAt(DateTimeOffset now) =>
        Status != DataSubjectRequestStatus.Closed && now > DueAt;

    public static DataSubjectRequest Create(CreateDataSubjectRequestParams parameters)
    {
        var now = DateTimeOffset.UtcNow;
        var receivedAt = parameters.ReceivedAt == default ? now : parameters.ReceivedAt;

        return new DataSubjectRequest
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            SubjectEmail = parameters.SubjectEmail.Trim().ToLowerInvariant(),
            Type = parameters.Type,
            Status = DataSubjectRequestStatus.Open,
            Disposition = DataSubjectRequestDisposition.None,
            Notes = string.IsNullOrWhiteSpace(parameters.Notes) ? null : parameters.Notes.Trim(),
            ReceivedAt = receivedAt,
            DueAt = receivedAt.Add(parameters.ResponseWindow),
            OpenedByStaffUserId = parameters.OpenedByStaffUserId,
        };
    }

    /// <summary>
    /// Appends a dated, attributed line to the working notes rather than overwriting them.
    /// </summary>
    public void AppendNote(string note, string staffUserId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        var line = $"[{at:u}] {staffUserId}: {note.Trim()}";
        Notes = string.IsNullOrWhiteSpace(Notes) ? line : Notes + "\n" + line;
    }

    /// <summary>Closes the request with a disposition.</summary>
    public void Close(DataSubjectRequestDisposition disposition, string staffUserId, DateTimeOffset at)
    {
        Status = DataSubjectRequestStatus.Closed;
        Disposition = disposition;
        ClosedAt ??= at;
        ClosedByStaffUserId ??= staffUserId;
        UpdatedAt = at;
    }

    /// <summary>Re-opens a closed request.</summary>
    public void Reopen(DataSubjectRequestStatus status, DateTimeOffset at)
    {
        Status = status;
        Disposition = DataSubjectRequestDisposition.None;
        ClosedAt = null;
        ClosedByStaffUserId = null;
        UpdatedAt = at;
    }
}
