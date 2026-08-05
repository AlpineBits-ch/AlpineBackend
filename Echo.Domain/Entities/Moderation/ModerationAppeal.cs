using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Moderation;

public class CreateAppealParams
{
    public required string ActionId { get; init; }
    public required string ContactEmail { get; init; }

    /// <summary>Set only when the appeal arrived from a signed-in session, which is the rarer case -
    /// a banned account cannot sign in.</summary>
    public string? SubmittedByUserId { get; init; }

    public string? Body { get; init; }
}

/// <summary>One appeal against one action.</summary>
public class ModerationAppeal : BaseEntity<ModerationAppeal>, IPrefixedEntity
{
    public static string Prefix => "apel";

    public const int MaxBodyLength = 2000;

    public string ActionId { get; set; } = null!;
    public ModerationAction? Action { get; set; }

    public string ContactEmail { get; set; } = null!;
    public string? SubmittedByUserId { get; set; }

    public string Body { get; set; } = string.Empty;

    public AppealStatus Status { get; set; } = AppealStatus.Pending;

    public string? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>The appellant's own reference for this appeal, distinct from the action's. Quoted
    /// back to check on status without a session.</summary>
    public string Reference { get; set; } = null!;

    public static ModerationAppeal Create(CreateAppealParams p, DateTimeOffset now)
    {
        var body = (p.Body ?? string.Empty).Trim();

        return new ModerationAppeal
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            ActionId = p.ActionId,
            // Lower-cased on the way in so "Name@Example.com" and "name@example.com" are the same
            // appellant at lookup time.
            ContactEmail = p.ContactEmail.Trim().ToLowerInvariant(),
            SubmittedByUserId = p.SubmittedByUserId,
            Body = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body,
            Status = AppealStatus.Pending,
            Reference = PublicReference.New(),
        };
    }

    public bool IsOpen => Status is AppealStatus.Pending or AppealStatus.UnderReview;

    /// <summary>Records the outcome.</summary>
    public void Decide(bool granted, string note, string staffUserId, DateTimeOffset now)
    {
        Status = granted ? AppealStatus.Granted : AppealStatus.Denied;
        DecisionNote = note.Trim();
        DecidedByUserId = staffUserId;
        DecidedAt = now;
        UpdatedAt = now;
    }

    public void Claim(string staffUserId, DateTimeOffset now)
    {
        if (Status != AppealStatus.Pending) return;

        Status = AppealStatus.UnderReview;
        DecidedByUserId = staffUserId;
        UpdatedAt = now;
    }
}
