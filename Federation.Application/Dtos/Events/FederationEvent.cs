using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Application.Dtos.Events.Inbound.Guild;
using Federation.Application.Dtos.Events.Outbound.Guild;

namespace Federation.Application.Dtos.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
[JsonDerivedType(typeof(MessageReceived), "messageReceived")]
// Add all derived types here on the BASE class
public  class FederationEvent
{
}

public class MessageReceived : FederationEvent
{
    public string Message { get; set; } = null!;
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
[JsonSerializable(typeof(MessageReceived))]
[JsonSerializable(typeof(List<FederationEvent>))]
public partial class EventJsonContext : JsonSerializerContext
{
}