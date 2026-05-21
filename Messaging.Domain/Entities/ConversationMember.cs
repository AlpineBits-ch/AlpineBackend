using System.ComponentModel.DataAnnotations.Schema;
using Messaging.Domain.Aggregates;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreateConversationMemberParams
{
    public string UserId { get; init; }
    public string ConversationId { get; init; }
    public byte[] PublicKey { get; init; }
    
    public string CachedUserName { get; init; }
    public int CachedUserHash { get; init; }
}
public class ConversationMember : BaseEntity<ConversationMember>, IPrefixedEntity
{
    public string ConversationId { get; set; }
    public virtual Conversation Conversation { get; set; }
    public string UserId { get; set; }
    
    [NotMapped] public static string Prefix { get; } = "cmem";
    
    public byte[] PublicKey { get; set; }
    
    public string CachedUserName { get; set; }
    public int CachedUserHash { get; set; }
    
    public string? LastReadMessageId { get; set; }
    public int MentionCount { get; set; }
    
    public string? FederatedServerId { get; set; }

    
    public virtual ICollection<ConversationMemberDevice> Devices { get; set; } = new List<ConversationMemberDevice>();


    public static ConversationMember Create(CreateConversationMemberParams createConversationMemberParams)
    {
        var id= GenerateId();
        var member = new ConversationMember
        {
            Id = id,
            ConversationId = createConversationMemberParams.ConversationId,
            UserId = createConversationMemberParams.UserId,
            PublicKey = createConversationMemberParams.PublicKey,
            CachedUserName = createConversationMemberParams.CachedUserName,
            CachedUserHash = createConversationMemberParams.CachedUserHash,
        };
        return member;
    }
}

public class ConversationMemberDevice : BaseEntity<ConversationMemberDevice>, IPrefixedEntity
{
    public virtual ConversationMember ConversationMember { get; set; }
    public string ConversationMemberId { get; set; }
    public string DeviceId { get; set; }
    public int MlsLeafIndex { get; set; }
    public static string Prefix { get; } = "cmde";
}