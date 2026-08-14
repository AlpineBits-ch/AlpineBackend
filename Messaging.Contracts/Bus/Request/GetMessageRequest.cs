namespace Messaging.Contracts.Bus.Request;

/// <summary>
/// How much of a message the caller is asking for, and therefore how much it has to be entitled to.
/// </summary>
public enum MessageReadScope
{
    /// <summary>The message and its body.</summary>
    Full = 0,

    /// <summary>Everything except the body: no content, no components, no embeds.</summary>
    MetadataOnly = 1,
}

/// <summary>Fetch one message by id.</summary>
public class GetMessageRequest
{
    public required string MessageId { get; init; }

    /// <summary>The user on whose behalf the read happens - never a service identity.</summary>
    public required string RequestingUserId { get; init; }

    public MessageReadScope Scope { get; init; } = MessageReadScope.Full;
}
