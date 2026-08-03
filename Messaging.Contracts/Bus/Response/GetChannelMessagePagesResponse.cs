namespace Messaging.Contracts.Bus.Response;

/// <summary>One message as the inbox renders it.</summary>
public class InboxMessagePreview
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string AuthorId { get; init; }

    /// <summary>Set for webhook and bot authors, which have no user record to look up.</summary>
    public string? AuthorDisplayName { get; init; }
    public string? AuthorAvatarUrl { get; init; }

    public byte[] Content { get; init; } = [];

    /// <summary>True when <see cref="Content"/> is ciphertext.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Which of the context's successive MLS groups the ciphertext was sealed to. Null on
    /// plaintext. Epochs restart per generation, so the epoch alone does not identify the group.</summary>
    public int? MlsGeneration { get; init; }

    /// <summary>Mirrors Messaging.Domain.Enums.MessageType as an integer - system messages carry no
    /// real content and the client renders a localized variant instead.</summary>
    public int Type { get; init; }

    public int? SystemMessageVariant { get; init; }

    public string? EmbedsJson { get; init; }
}

public class ChannelMessagePage
{
    public required string ChannelId { get; init; }

    /// <summary>Oldest-first, matching the history endpoint, so a client can append them under the
    /// group in reading order.</summary>
    public required IReadOnlyList<InboxMessagePreview> Messages { get; init; }
}

public class GetChannelMessagePagesResponse
{
    public required IReadOnlyList<ChannelMessagePage> Pages { get; init; }
}
