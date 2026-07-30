namespace Messaging.Contracts.Bus.Request;

/// <summary>
/// Fetch one message by id. Added for the component-interaction path in Bots, which has to verify
/// that an incoming custom_id actually belongs to the message the client claims it clicked - and
/// Bots has no access to the message store.
/// </summary>
public class GetMessageRequest
{
    public string MessageId { get; set; } = null!;
}
