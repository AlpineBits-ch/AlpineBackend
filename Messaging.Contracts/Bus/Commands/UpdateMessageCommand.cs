namespace Messaging.Contracts.Bus.Commands;

public class UpdateMessageCommand
{
    public string MessageId { get; set; }
    public string RequestingAuthorId { get; set; }
    public byte[] Content { get; set; }
    public string? EmbedsJson { get; set; }
}
