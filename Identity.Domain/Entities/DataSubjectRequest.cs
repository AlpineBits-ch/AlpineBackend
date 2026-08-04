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

    /// <summary>The statutory response window. Passed in rather than hard-coded so the one place
    /// that knows the jurisdiction decides it.</summary>
    public TimeSpan ResponseWindow { get; init; }
}

/// <summary>
/// A rights request that arrived out of band - by email, by post, or from someone who has no account
/// here at all (T1-13).
///
/// <para><b>Keyed by email, not by user id, on purpose.</b> The requests that most need tracking are
/// exactly the ones where the subject cannot be resolved to an account: a former user whose account
/// is already tombstoned, someone who never registered but whose address appears in an invite, or a
/// person acting for someone else. A nullable FK to <c>asp_net_users</c> would have made the
/// unresolvable case look like a data error.</para>
///
/// <para><b>The clock is the point.</b> An unanswered request is the violation; a wrong answer is a
/// dispute. <see cref="DueAt"/> is stamped once at intake from the arrival time and never
/// recalculated, so reassigning or re-opening a request cannot quietly buy another thirty days.</para>
/// </summary>
public class DataSubjectRequest : BaseEntity<DataSubjectRequest>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "dsrq";

    /// <summary>Address the request came from / concerns. Lower-cased on intake so the queue does
    /// not show the same person twice.</summary>
    public string SubjectEmail { get; set; } = null!;

    /// <summary>The account this was matched to, when one could be matched. Advisory - the request
    /// is valid and trackable without it.</summary>
    public string? SubjectUserId { get; set; }

    public DataSubjectRequestType Type { get; set; }

    public DataSubjectRequestStatus Status { get; set; } = DataSubjectRequestStatus.Open;

    public DataSubjectRequestDisposition Disposition { get; set; } = DataSubjectRequestDisposition.None;

    /// <summary>Free-text working notes. Appended to, not replaced, by each update - see
    /// <see cref="AppendNote"/>.</summary>
    public string? Notes { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Statutory deadline, stamped once at intake.</summary>
    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The staff account that opened the request. Every mutation is also written to
    /// <c>IdentityAuditEvent</c> with the acting staff id - this column is the convenience copy the
    /// queue view renders, not the audit trail.</summary>
    public string OpenedByStaffUserId { get; set; } = null!;

    public string? AssignedToStaffUserId { get; set; }

    public string? ClosedByStaffUserId { get; set; }

    /// <summary>Past its deadline and still unanswered. Computed rather than stored so it cannot go
    /// stale, and false once closed even if the close was late - lateness of a closed request is
    /// visible from <see cref="ClosedAt"/> against <see cref="DueAt"/>.</summary>
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
    ///
    /// <para>Overwriting was the obvious shape and is wrong: the notes on a rights request are the
    /// contemporaneous record of what was done about it, and a queue where the last editor can erase
    /// the reasoning of the previous one answers "what did we decide" with whatever was typed
    /// most recently.</para>
    /// </summary>
    public void AppendNote(string note, string staffUserId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        var line = $"[{at:u}] {staffUserId}: {note.Trim()}";
        Notes = string.IsNullOrWhiteSpace(Notes) ? line : Notes + "\n" + line;
    }

    /// <summary>Closes the request with a disposition. Idempotent: a redundant close keeps the
    /// original <see cref="ClosedAt"/>, so the recorded answer time is when it was actually
    /// answered.</summary>
    public void Close(DataSubjectRequestDisposition disposition, string staffUserId, DateTimeOffset at)
    {
        Status = DataSubjectRequestStatus.Closed;
        Disposition = disposition;
        ClosedAt ??= at;
        ClosedByStaffUserId ??= staffUserId;
        UpdatedAt = at;
    }

    /// <summary>Re-opens a closed request. The deadline is <b>not</b> extended - see
    /// <see cref="DueAt"/>.</summary>
    public void Reopen(DataSubjectRequestStatus status, DateTimeOffset at)
    {
        Status = status;
        Disposition = DataSubjectRequestDisposition.None;
        ClosedAt = null;
        ClosedByStaffUserId = null;
        UpdatedAt = at;
    }
}
