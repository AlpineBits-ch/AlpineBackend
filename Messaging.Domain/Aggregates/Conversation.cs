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

    /// <summary>
    /// Null for a locally-created conversation. Set to the owning Federation.Application
    /// instance's FederationInstance id when this conversation is a shadow copy materialized
    /// from a remote instance (see the canonical-ID / shadow-entity model in the federation
    /// protocol doc) - doubles as the "is this remote" flag.
    /// </summary>
    public string? OriginInstanceId { get; set; }

    public static Conversation Create(CreateConversationParams parameters)
    {
        if (parameters.Encryption == ChannelEncryptionState.Encrypted)
        {
            if (parameters.MlsGroupId is null or { Length: 0 })
                throw new ArgumentException("An encrypted conversation needs an MLS group id", nameof(parameters));

            // Epoch 0 is a real, valid state: a group whose creator has not added anyone yet sits at
            // epoch 0 until the first commit. Rejecting it (the old check was `== 0`) turned every
            // encrypted conversation with no reachable invitee device into a 500 instead of a group
            // the creator could add members to later.
            if (parameters.MlsEpoch is null or < 0)
                throw new ArgumentException("An encrypted conversation needs a non-negative MLS epoch", nameof(parameters));

            // GroupInfo is deliberately optional. It exists so a device that fell further behind
            // than the retained commits can rejoin by external commit - a recovery aid, not
            // something the group's correctness depends on, and a group at epoch 0 has nothing
            // worth publishing yet.
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