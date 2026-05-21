using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Application.Dtos.Events.Inbound.Guild;
using Federation.Application.Dtos.Events.Outbound.Guild;
using Federation.Domain.Aggregates;
using NSec.Cryptography;

namespace Federation.Application.Dtos.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
[JsonDerivedType(typeof(MessageCreated), "messageCreated")]
[JsonDerivedType(typeof(MessageEdited), "messageEdited")]
[JsonDerivedType(typeof(MessageDeleted), "messageDeleted")]
[JsonDerivedType(typeof(MessageReactionAdded), "messageReactionAdded")]
[JsonDerivedType(typeof(MessageReactionRemoved), "messageReactionRemoved")]

public  class FederationEvent
{
    public string Host { get; set; }
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

    public static SignedFederationEvent Create(FederationEvent payload)
    {
        payload.Host = Env.GeneralConfiguration.InstanceUrl;
        var algorithm = SignatureAlgorithm.Ed25519;
    
        var privateKeyBytes = (Env.Federation.PrivateKey);
    
        var key = Key.Import(algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
    
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

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, federationPublicKey, KeyBlobFormat.RawPublicKey);
        var isValid = algorithm.Verify(publicKey, JsonSerializer.SerializeToUtf8Bytes(Payload), Signature);
        
        return isValid;
    }
    
    
}