using Messaging.Domain.Enums;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreateAttachmentParams
{
    public string Id { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string Url { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string CreatorId { get; set; }
}

public class Attachment : BaseEntity<Attachment>, IPrefixedEntity
{
    public static string GetCacheId(string id)
    {
        return $"attachment:{id}:download";
    }
    
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string Url { get; set; }
    public string? ThumbnailUrl { get; set; }
    
    public string? ThumbnailId { get; set; }
    
    public string CreatorId { get; set; }

    /// <summary>
    /// The channel or conversation of the message this attachment was sent in, stamped when that
    /// message is created (see CreateMessageCommand).
    /// </summary>
    public string? ContextId { get; set; }

    public AttachmentState State { get; set; } = AttachmentState.Pending;
    
    public static Attachment Create(CreateAttachmentParams createAttachmentParams)
    {
        string id;

        if (!string.IsNullOrEmpty(createAttachmentParams.Id))
        {
            id = createAttachmentParams.Id;
        }
        else
        {
            id = GenerateId();
        }
        var createdDate = DateTimeOffset.UtcNow;
        return new Attachment
        {
            Id = id,
            CreatedAt = createdDate,
            UpdatedAt = createdDate,
            FileName = createAttachmentParams.FileName,
            ContentType = createAttachmentParams.ContentType,
            SizeBytes = createAttachmentParams.SizeBytes,
            Url = createAttachmentParams.Url,
            ThumbnailUrl = createAttachmentParams.ThumbnailUrl,
            CreatorId = createAttachmentParams.CreatorId
        };
    }
    public static string Prefix { get; } = "atac";
}


public class CreateMinimalAttachmentParams
{
    public string Id { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public string? ThumbnailUrl { get; set; } = "";
    public string? ThumbnailId { get; set; }
}

public class MinimalAttachment : BaseEntity<MinimalAttachment>, IPrefixedEntity
{
    public static string Prefix { get; } = "mata";
    
    public string FileName { get; init; }
    public string ContentType { get; init; }
    public string? ThumbnailUrl { get; init; } = "";
    public string? ThumbnailId { get; init; }
    
    public static string GetCacheId(string id)
    {
        return $"attachment:{id}:thumb";
    }

    public static MinimalAttachment Create(CreateMinimalAttachmentParams createMinimalAttachmentParams)
    {
        var createdDate = DateTimeOffset.UtcNow;
        return new MinimalAttachment
        {
            Id = createMinimalAttachmentParams.Id,
            CreatedAt = createdDate,
            UpdatedAt = createdDate,
            FileName = createMinimalAttachmentParams.FileName,
            ContentType = createMinimalAttachmentParams.ContentType,
            ThumbnailUrl = createMinimalAttachmentParams.ThumbnailUrl,
            ThumbnailId = createMinimalAttachmentParams.ThumbnailId
        };
    }
}