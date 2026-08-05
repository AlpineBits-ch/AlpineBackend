using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Moderation;

public class CreateReportParams
{
    /// <summary>Null when staff opened the report themselves off a support ticket.</summary>
    public string? ReporterUserId { get; init; }

    public required string TargetUserId { get; init; }
    public ReportSubjectKind SubjectKind { get; init; }
    public string? SubjectId { get; init; }
    public ReportReason Reason { get; init; }
    public string? Details { get; init; }
    public string? EvidenceJson { get; init; }
}

/// <summary>
/// One filed report. Immutable except for the triage fields staff write.
///
/// <para><b>On <see cref="EvidenceJson"/>.</b> The snapshot is whatever the reporting client
/// captured from its own view: it is what the reporter <em>says</em> they saw, attested by nothing,
/// and the console must label it that way.
///
/// <para><b>Encryption is per conversation and off by default</b> - see
/// <c>Conversation.EncryptionState</c>, which starts at <c>Plain</c> and only becomes
/// <c>Encrypted</c> when the client creates the conversation with an MLS group. So the two cases
/// differ, and the difference matters to a moderator:</para>
///
/// <list type="bullet">
///   <item>Plain conversation: the server holds the message text, so the snapshot <em>could</em> be
///   corroborated against it. Nothing here does that yet - the console shows the snapshot alone -
///   which is why the evidence is still labelled unverified rather than confirmed.</item>
///   <item>Encrypted conversation: the server holds ciphertext and cannot produce the plaintext at
///   review time at all. The snapshot is the only thing that will ever exist, and no amount of
///   server-side work changes that without breaking the guarantee the encryption exists for.</item>
/// </list>
///
/// <para>The reporting client states which case it was, and the console must not present either as
/// server-attested message text.</para>
/// </summary>
public class ModerationReport : BaseEntity<ModerationReport>, IPrefixedEntity
{
    public static string Prefix => "rprt";

    public const int MaxDetailsLength = 4000;

    /// <summary>16 KB.</summary>
    public const int MaxEvidenceLength = 16 * 1024;

    public string? ReporterUserId { get; set; }
    public string TargetUserId { get; set; } = null!;

    public ReportSubjectKind SubjectKind { get; set; }
    public string? SubjectId { get; set; }

    public ReportReason Reason { get; set; }
    public string Details { get; set; } = string.Empty;
    public string? EvidenceJson { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public ReportPriority Priority { get; set; } = ReportPriority.Normal;

    public string? AssignedToUserId { get; set; }
    public string? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
    public string? DuplicateOfId { get; set; }

    /// <summary>Bumped when a duplicate is folded into this one, so the queue can show that five
    /// people reported the same thing without carrying five rows through triage.</summary>
    public int DuplicateCount { get; set; }

    public static ModerationReport Create(CreateReportParams p, DateTimeOffset now)
    {
        var details = (p.Details ?? string.Empty).Trim();

        return new ModerationReport
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            ReporterUserId = p.ReporterUserId,
            TargetUserId = p.TargetUserId,
            SubjectKind = p.SubjectKind,
            // A subject id on a User report would be a value nothing reads and everything has to
            // decide whether to trust; dropped here rather than at each reader.
            SubjectId = p.SubjectKind == ReportSubjectKind.User ? null : p.SubjectId,
            Reason = p.Reason,
            Details = details.Length > MaxDetailsLength ? details[..MaxDetailsLength] : details,
            EvidenceJson = p.EvidenceJson,
            Status = ReportStatus.Open,
            Priority = PriorityFor(p.Reason),
        };
    }

    /// <summary>The queue position a reason earns on arrival.</summary>
    public static ReportPriority PriorityFor(ReportReason reason) => reason switch
    {
        ReportReason.ChildSafety or ReportReason.SelfHarm or ReportReason.ViolentThreats
            => ReportPriority.Critical,

        ReportReason.HateSpeech or ReportReason.Harassment or ReportReason.IllegalContent
        or ReportReason.Malware or ReportReason.SexualContent
            => ReportPriority.High,

        ReportReason.Spam or ReportReason.Impersonation => ReportPriority.Normal,

        _ => ReportPriority.Low,
    };

    /// <summary>Whether this report still needs someone. Drives every "open work" count.</summary>
    public bool IsOpen => Status is ReportStatus.Open or ReportStatus.Triaged;

    public void Assign(string? staffUserId, DateTimeOffset now)
    {
        AssignedToUserId = staffUserId;

        // Picking a report up is what moves it out of the unclaimed queue.
        if (staffUserId is not null && Status == ReportStatus.Open) Status = ReportStatus.Triaged;

        UpdatedAt = now;
    }

    /// <summary>Closes the report.</summary>
    public void Resolve(ReportStatus status, string resolution, string staffUserId, DateTimeOffset now)
    {
        Status = status;
        Resolution = resolution;
        ResolvedByUserId = staffUserId;
        ResolvedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Reopens a closed report, clearing the resolution but keeping the assignee.</summary>
    public void Reopen(DateTimeOffset now)
    {
        Status = AssignedToUserId is null ? ReportStatus.Open : ReportStatus.Triaged;
        Resolution = null;
        ResolvedByUserId = null;
        ResolvedAt = null;
        DuplicateOfId = null;
        UpdatedAt = now;
    }
}
