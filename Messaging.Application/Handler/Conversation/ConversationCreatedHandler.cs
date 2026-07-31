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

        var welcomes = await context.PendingWelcomes
            .Where(w => w.ConversationId == @event.ConversationId && w.ConsumedAt == null)
            .ToListAsync();

        // Addressed to the one device holding the matching leaf, not to every session the user has
        // open - the fetch behind this push is device-scoped, so waking a user's other devices only
        // costs them a round-trip that can never return anything.
        foreach (var welcome in welcomes)
        {
            await hubContext.Clients
                .Group(EchoRealtimeHub.DeviceGroup(welcome.UserId, welcome.DeviceId))
                .SendAsync("conversation.Welcome", @event.ConversationId);
        }

        foreach (var member in members)
        {
            await permissionService.GetPermissionsForUser(member.UserId, rebuild: true);
        }

        await hubContext.Clients.Users(members.Select(m => m.UserId)).SendAsync("conversation.ConversationCreated", @event.ConversationId);
    }
}