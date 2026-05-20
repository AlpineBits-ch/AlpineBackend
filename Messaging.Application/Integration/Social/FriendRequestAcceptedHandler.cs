using System.Reflection.Metadata;
using Messaging.Application.Hubs;
using Messaging.Application.Services;
using Microsoft.AspNetCore.SignalR;
using Social.Contracts.Bus.Integration.Events;

namespace Messaging.Application.Integration.Social;

public class FriendRequestAcceptedHandler
{
    public static async Task Handle(FriendshipAcceptedEvent acceptedEvent, IHubContext<MessagingHub> hubContext, ConversationPermissionService conversationPermissionService)
    {
        await hubContext.Clients.User(acceptedEvent.InitiatorUserId).SendAsync("FriendRequestAccepted", acceptedEvent);

        
        // FIX: this rebuilds the cache so that new conversations can be created
        await conversationPermissionService.GetPermissionsForUser(acceptedEvent.InitiatorUserId, rebuild: true);
        await conversationPermissionService.GetPermissionsForUser(acceptedEvent.AcceptantUserId, rebuild: true);
    }
}