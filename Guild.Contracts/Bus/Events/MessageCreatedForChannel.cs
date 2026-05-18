namespace Guild.Contracts.Bus.Events;

public enum MessageEncryptionState
{
    Plain,
    Encrypted
}

public class MessageCreatedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; }
    
    public ICollection<string> Mentions { get; set; } = new List<string>();
    
    public ICollection<MinimalAttachmentForChannel> Attachments { get; set; } = new List<MinimalAttachmentForChannel>();
}


public class MinimalAttachmentForChannel
{
    public string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string FileName { get; init; }
    public string ContentType { get; init; }
    public string? ThumbnailUrl { get; init; } = "";
    public string? ThumbnailId { get; init; }
    
    public static string GetCacheId(string id)
    {
        return $"attachment:{id}:thumb";
    }

}