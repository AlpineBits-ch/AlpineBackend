using System.Security.Cryptography;
using System.Text;
using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Moderation;

public class CreateSupportTicketParams
{
    public required string ContactEmail { get; init; }
    public required string Subject { get; init; }
    public SupportTicketCategory Category { get; init; }
    public string? RequesterUserId { get; init; }
}

/// <summary>A support conversation.</summary>
public class SupportTicket : BaseEntity<SupportTicket>, IPrefixedEntity
{
    public static string Prefix => "supt";

    public const int MaxSubjectLength = 200;

    public string ContactEmail { get; set; } = null!;
    public string? RequesterUserId { get; set; }

    public string Subject { get; set; } = null!;
    public SupportTicketCategory Category { get; set; }
    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;

    public string? AssignedToUserId { get; set; }

    public string Reference { get; set; } = null!;

    /// <summary>SHA-256 of the access token.</summary>
    public byte[] AccessTokenHash { get; set; } = [];

    /// <summary>Drives the "oldest waiting" sort.</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    public ICollection<SupportTicketMessage> Messages { get; set; } = new List<SupportTicketMessage>();

    /// <summary>Creates the ticket and returns the one-time access token beside it.</summary>
    public static (SupportTicket Ticket, string Token) Create(CreateSupportTicketParams p, DateTimeOffset now)
    {
        var token = Base64UrlToken();
        var subject = p.Subject.Trim();

        var ticket = new SupportTicket
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            ContactEmail = p.ContactEmail.Trim().ToLowerInvariant(),
            RequesterUserId = p.RequesterUserId,
            Subject = subject.Length > MaxSubjectLength ? subject[..MaxSubjectLength] : subject,
            Category = p.Category,
            Status = SupportTicketStatus.Open,
            Reference = PublicReference.New(),
            AccessTokenHash = HashToken(token),
            LastActivityAt = now,
        };

        return (ticket, token);
    }

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Fixed-time compare.</summary>
    public bool TokenMatches(string? token)
    {
        if (string.IsNullOrEmpty(token) || AccessTokenHash.Length == 0) return false;

        return CryptographicOperations.FixedTimeEquals(AccessTokenHash, HashToken(token));
    }

    private static string Base64UrlToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool IsOpen => Status is not (SupportTicketStatus.Resolved or SupportTicketStatus.Closed);

    /// <summary>
    /// Appends a message and moves the ticket to whichever side now owes a reply.
    /// </summary>
    public SupportTicketMessage Append(
        SupportMessageAuthorKind authorKind, string? authorUserId, string body, bool internalNote,
        DateTimeOffset now)
    {
        var message = SupportTicketMessage.Create(Id, authorKind, authorUserId, body, internalNote, now);
        Messages.Add(message);

        UpdatedAt = now;

        if (internalNote) return message;

        LastActivityAt = now;

        Status = authorKind switch
        {
            SupportMessageAuthorKind.Requester => SupportTicketStatus.AwaitingStaff,
            SupportMessageAuthorKind.Staff => SupportTicketStatus.AwaitingRequester,
            _ => Status,
        };

        return message;
    }

    public void SetStatus(SupportTicketStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}

public class SupportTicketMessage : BaseEntity<SupportTicketMessage>, IPrefixedEntity
{
    public static string Prefix => "supm";

    public const int MaxBodyLength = 8000;

    public string TicketId { get; set; } = null!;
    public SupportTicket? Ticket { get; set; }

    public SupportMessageAuthorKind AuthorKind { get; set; }
    public string? AuthorUserId { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>Staff-only.</summary>
    public bool IsInternal { get; set; }

    public static SupportTicketMessage Create(
        string ticketId, SupportMessageAuthorKind authorKind, string? authorUserId, string body,
        bool internalNote, DateTimeOffset now)
    {
        var text = body.Trim();

        return new SupportTicketMessage
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            TicketId = ticketId,
            AuthorKind = authorKind,
            AuthorUserId = authorUserId,
            Body = text.Length > MaxBodyLength ? text[..MaxBodyLength] : text,
            // Only staff can leave one.
            IsInternal = internalNote && authorKind == SupportMessageAuthorKind.Staff,
        };
    }
}
