using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Moderation;

public class CreateModerationActionParams
{
    public required string TargetUserId { get; init; }
    public required string ActorUserId { get; init; }
    public ModerationActionKind Kind { get; init; }
    public ReportReason Reason { get; init; }

    /// <summary>What the user is told.</summary>
    public string? PublicNote { get; init; }

    public string? InternalNote { get; init; }

    /// <summary>Null on a Ban means permanent.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public string? ReportId { get; init; }
}

/// <summary>One thing staff did to an account.</summary>
public class ModerationAction : BaseEntity<ModerationAction>, IPrefixedEntity
{
    public static string Prefix => "mact";

    public const int MaxNoteLength = 2000;

    public string TargetUserId { get; set; } = null!;
    public string ActorUserId { get; set; } = null!;

    public ModerationActionKind Kind { get; set; }
    public ReportReason Reason { get; set; }

    public string PublicNote { get; set; } = string.Empty;
    public string? InternalNote { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedByUserId { get; set; }
    public string? RevocationReason { get; set; }

    public string? ReportId { get; set; }

    /// <summary>The code the user quotes to appeal this. See <see cref="PublicReference"/>.</summary>
    public string Reference { get; set; } = null!;

    public ICollection<ModerationAppeal> Appeals { get; set; } = new List<ModerationAppeal>();

    public static ModerationAction Create(CreateModerationActionParams p, DateTimeOffset now)
    {
        // A suspension with no end date is a ban wearing a friendlier word, and the difference
        // matters to the person it lands on.
        if (p.Kind == ModerationActionKind.Suspension && p.ExpiresAt is null)
            throw new ArgumentException("A suspension must have an expiry.", nameof(p));

        if (p.Kind is ModerationActionKind.Unban or ModerationActionKind.Note && p.ExpiresAt is not null)
            throw new ArgumentException($"A {p.Kind} action cannot expire.", nameof(p));

        return new ModerationAction
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            TargetUserId = p.TargetUserId,
            ActorUserId = p.ActorUserId,
            Kind = p.Kind,
            Reason = p.Reason,
            PublicNote = Trim(p.PublicNote),
            InternalNote = string.IsNullOrWhiteSpace(p.InternalNote) ? null : Trim(p.InternalNote),
            ExpiresAt = p.ExpiresAt,
            ReportId = p.ReportId,
            Reference = PublicReference.New(),
        };
    }

    private static string Trim(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > MaxNoteLength ? text[..MaxNoteLength] : text;
    }

    /// <summary>Whether this action is still in force at <paramref name="now"/>.</summary>
    public bool IsActiveAt(DateTimeOffset now) =>
        Kind is ModerationActionKind.Ban or ModerationActionKind.Suspension
        && RevokedAt is null
        && (ExpiresAt is null || ExpiresAt > now);

    /// <summary>Whether the sanction ran its course rather than being lifted early.</summary>
    public bool HasExpiredAt(DateTimeOffset now) =>
        RevokedAt is null && ExpiresAt is not null && ExpiresAt <= now;

    /// <summary>Lifts a live sanction.</summary>
    public bool Revoke(string staffUserId, string? reason, DateTimeOffset now)
    {
        if (RevokedAt is not null) return false;
        if (Kind is not (ModerationActionKind.Ban or ModerationActionKind.Suspension)) return false;

        RevokedAt = now;
        RevokedByUserId = staffUserId;
        RevocationReason = string.IsNullOrWhiteSpace(reason) ? null : Trim(reason);
        UpdatedAt = now;
        return true;
    }

    /// <summary>Whether a user may still appeal this.</summary>
    public bool IsAppealableAt(DateTimeOffset now) =>
        Kind is ModerationActionKind.Ban or ModerationActionKind.Suspension
        && RevokedAt is null
        && (ExpiresAt is null || ExpiresAt > now);
}
