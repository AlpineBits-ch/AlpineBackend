using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Domain.Aggregates;
using NSec.Cryptography;

namespace Federation.Application.Dtos.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
// Messaging
[JsonDerivedType(typeof(MessageCreated), "messageCreated")]
[JsonDerivedType(typeof(MessageEdited), "messageEdited")]
[JsonDerivedType(typeof(MessageDeleted), "messageDeleted")]
[JsonDerivedType(typeof(MessageReactionAdded), "messageReactionAdded")]
[JsonDerivedType(typeof(MessageReactionRemoved), "messageReactionRemoved")]
// Guild (bidirectional)
[JsonDerivedType(typeof(GuildMemberJoined), "guildMemberJoined")]
[JsonDerivedType(typeof(GuildMemberLeft), "guildMemberLeft")]
[JsonDerivedType(typeof(GuildMemberBanned), "guildMemberBanned")]
// Guild (inbound)
[JsonDerivedType(typeof(GuildInviteRedeemed), "guildInviteRedeemed")]
[JsonDerivedType(typeof(GuildJoinRequest), "guildJoinRequest")]
// Guild (outbound)
[JsonDerivedType(typeof(GuildInviteAccepted), "guildInviteAccepted")]
[JsonDerivedType(typeof(GuildInviteRevoked), "guildInviteRevoked")]
// Social
[JsonDerivedType(typeof(SocialFriendRequest), "socialFriendRequest")]
[JsonDerivedType(typeof(SocialFriendAccepted), "socialFriendAccepted")]
[JsonDerivedType(typeof(SocialFriendRejected), "socialFriendRejected")]
[JsonDerivedType(typeof(SocialFriendRemoved), "socialFriendRemoved")]
// Conversation
[JsonDerivedType(typeof(ConversationCreated), "conversationCreated")]
[JsonDerivedType(typeof(ConversationEdited), "conversationEdited")]
[JsonDerivedType(typeof(ConversationDeleted), "conversationDeleted")]
[JsonDerivedType(typeof(ConversationMemberAdded), "conversationMemberAdded")]
[JsonDerivedType(typeof(ConversationMemberLeft), "conversationMemberLeft")]
public class FederationEvent
{
    public string Host { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string[] PreviousEventIds { get; set; } = [];
    public long Depth { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public DateTime OriginServerTime { get; set; }
}


[JsonSerializable(typeof(ConversationCreated))]
[JsonSerializable(typeof(ConversationDeleted))]
[JsonSerializable(typeof(ConversationEdited))]
[JsonSerializable(typeof(ConversationMemberAdded))]
[JsonSerializable(typeof(ConversationMemberLeft))]

[JsonSerializable(typeof(SocialFriendAccepted))]
[JsonSerializable(typeof(SocialFriendRejected))]
[JsonSerializable(typeof(SocialFriendRemoved))]
[JsonSerializable(typeof(SocialFriendRequest))]

[JsonSerializable(typeof(MessageCreated))]
[JsonSerializable(typeof(MessageDeleted))]
[JsonSerializable(typeof(MessageEdited))]
[JsonSerializable(typeof(MessageReactionAdded))]
[JsonSerializable(typeof(MessageReactionRemoved))]

[JsonSerializable(typeof(GuildMemberJoined))]
[JsonSerializable(typeof(GuildMemberLeft))]
[JsonSerializable(typeof(GuildInviteRedeemed))]
[JsonSerializable(typeof(GuildJoinRequest))]

[JsonSerializable(typeof(GuildInviteAccepted))]
[JsonSerializable(typeof(GuildInviteRevoked))]
[JsonSerializable(typeof(GuildJoinRequest))]
[JsonSerializable(typeof(GuildMemberBanned))]


[JsonSerializable(typeof(FederationEvent))]
[JsonSerializable(typeof(List<FederationEvent>))]
public partial class EventJsonContext : JsonSerializerContext
{
}

public class SignedFederationEvent
{
    public required FederationEvent Payload { get; set; }
    public required byte[] Signature { get; set; }

    public static SignedFederationEvent Create(FederationEvent payload, string protocolVersion)
    {
        payload.Host = Env.GeneralConfiguration.InstanceUrl;
        payload.ProtocolVersion = protocolVersion;
        var algorithm = SignatureAlgorithm.Ed25519;
    
        var privateKeyBytes = (Env.Federation.PrivateKey);
    
        var key = Key.Import(algorithm, privateKeyBytes, KeyBlobFormat.PkixPrivateKeyText);
    
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = algorithm.Sign(key, payloadBytes);
        
      
        
        return new SignedFederationEvent
        {
            Payload = payload,
            Signature = signature,
        };
    }

    public bool IsValid(FederationInstance instance)
    {
        var federationPublicKey = instance.PublicKey;
        
        var algorithm = SignatureAlgorithm.Ed25519;

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, federationPublicKey, KeyBlobFormat.PkixPublicKeyText);
        var isValid = algorithm.Verify(publicKey, JsonSerializer.SerializeToUtf8Bytes(Payload), Signature);
        
        return isValid;
    }
    
    
}