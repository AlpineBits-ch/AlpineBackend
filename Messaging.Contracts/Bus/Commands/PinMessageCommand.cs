namespace Messaging.Contracts.Bus.Commands;

public class PinMessageCommand
{
    public string MessageId { get; set; }
    public string RequestingUserId { get; set; }
}

public class UnpinMessageCommand
{
    public string MessageId { get; set; }
    public string RequestingUserId { get; set; }
}
