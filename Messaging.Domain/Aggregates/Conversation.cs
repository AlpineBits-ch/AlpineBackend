using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Conversation;
using Persistence;

namespace Messaging.Domain.Aggregates;



public class CreateConversationParams
{
    public string? Name { get; init; }
    public ChannelEncryptionState Encryption { get; init; }
    public ICollection<CreateConversationMemberParams> Members { get; init; } = new List<CreateConversationMemberParams>();
    
    public byte[]? MlsGroupId { get; init; }
    public long? MlsEpoch { get; init; }
    public byte[]? MlsGroupInfo { get; init; }
    
}

public class Conversation : Aggregate<Conversation>, IPrefixedEntity
{
    public string? Name { get; set; }
    public virtual ICollection<ConversationMember> Members { get; set; } = new List<ConversationMember>();
    
    [NotMapped] public static string Prefix { get; } = "conv";
    
    public ChannelEncryptionState EncryptionState { get; set; } = ChannelEncryptionState.Plain;


    public byte[]? MlsGroupId { get; set; }
    public long? MlsEpoch { get; set; }
    public byte[]? MlsGroupInfo { get; set; }

    /// <summary>Null for a locally-created conversation.</summary>
    public string? OriginInstanceId { get; set; }

    /// <summary>When the group icon was last written, or null when there is none. Clients also read
    /// it as the icon URL's cache key, which UpdatedAt cannot be: that moves on every message.</summary>
    public DateTimeOffset? IconUpdatedAt { get; set; }

    public static Conversation Create(CreateConversationParams parameters)
    {
        if (parameters.Encryption == ChannelEncryptionState.Encrypted)
        {
            if (parameters.MlsGroupId is null or { Length: 0 })
                throw new ArgumentException("An encrypted conversation needs an MLS group id", nameof(parameters));

            // Epoch 0 is a real, valid state: a group whose creator has not added anyone yet sits
            // at epoch 0 until the first commit.
            if (parameters.MlsEpoch is null or < 0)
                throw new ArgumentException("An encrypted conversation needs a non-negative MLS epoch", nameof(parameters));

            // GroupInfo is deliberately optional.
            if (parameters.MlsGroupInfo is { Length: 0 })
                throw new ArgumentException("MLS group info must be non-empty when supplied", nameof(parameters));
        }
        var id = GenerateId();
        var conversation = new Conversation
        {
            Id = id,
            Name = parameters.Name,
            EncryptionState = parameters.Encryption,
            Members = parameters.Members.Select(ConversationMember.Create).ToList(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MlsEpoch = parameters.MlsEpoch,
            MlsGroupId = parameters.MlsGroupId,
            MlsGroupInfo = parameters.MlsGroupInfo,
        };
        
        conversation.AddDomainEvent(new ConversationCreated()
        {
            ConversationId = id,
            CorrelationId = id,
            MemberIds = conversation.Members.Select(m => m.UserId).ToArray(),
        });
        
        foreach (var conversationMember in conversation.Members)
        {
            conversation.AddDomainEvent(new ConversationMemberAdded()
            {
                ConversationId = id,
                UserId = conversationMember.UserId,
                CorrelationId = id,
            });
        }
        return conversation;
    }

}