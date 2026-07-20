using Echo.Realtime;
using Messaging.Application.Services;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Conversation;

public class ConversationCreatedHandler
{
    public static async Task Handle(ConversationCreated @event, MicroserviceContext context, ConversationPermissionService permissionService, IHubContext<EchoRealtimeHub> hubContext)
    {
        var members = await context.Members
            .Where(m => m.ConversationId == @event.ConversationId)
            .ToListAsync();

        var welcomes = await context.PendingWelcomes.Where(w => w.ConversationId == @event.ConversationId).ToListAsync();

        foreach (var welcome in welcomes)
        {
            await hubContext.Clients.User(welcome.UserId).SendAsync("conversation.Welcome", @event.ConversationId);
        }
        
        foreach (var member in members)
        {
            await permissionService.GetPermissionsForUser(member.UserId, rebuild: true);
        }
        
        await hubContext.Clients.Users(members.Select(m => m.UserId)).SendAsync("conversation.ConversationCreated", @event.ConversationId);
        
        
        // Here we send the welcome packages directly, if the user is online.
        
        
        
    }
}