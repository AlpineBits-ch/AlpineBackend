namespace Messaging.Domain.Events;

public class ProcessAttachment
{
    public string AttachmentId { get; set; }
    public string ContentType { get; set; }
}