namespace Messaging.Contracts.Bus.Request;

/// <summary>Fetch one message by id.</summary>
public class GetMessageRequest
{
    public string MessageId { get; set; } = null!;
}
