using System.Reflection.Metadata;
using Echo.Realtime;
using Messaging.Application.Services;
using Microsoft.AspNetCore.SignalR;
using Social.Contracts.Bus.Integration.Events;

namespace Messaging.Application.Integration.Social;

public class FriendRequestAcceptedHandler
{
    public static async Task Handle(FriendshipAcceptedEvent acceptedEvent, IHubContext<EchoRealtimeHub> hubContext, ConversationPermissionService conversationPermissionService)
    {
        // Superseded by social.FriendRequestAccepted (Social.Application's
        // RelationshipRealtimeHandlers), which reaches both parties instead of just the initiator
        // and carries each side's own relationship row.
        await hubContext.Clients.User(acceptedEvent.InitiatorUserId).SendAsync("conversation.FriendRequestAccepted", acceptedEvent);


        // FIX: this rebuilds the cache so that new conversations can be created
        await conversationPermissionService.GetPermissionsForUser(acceptedEvent.InitiatorUserId, rebuild: true);
        await conversationPermissionService.GetPermissionsForUser(acceptedEvent.AcceptantUserId, rebuild: true);
    }
}