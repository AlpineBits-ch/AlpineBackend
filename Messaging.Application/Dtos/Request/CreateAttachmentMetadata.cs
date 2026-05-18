namespace Messaging.Application.Dtos.Request;

public class CreateAttachmentMetadata
{
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public long SizeBytes { get; set; }
}