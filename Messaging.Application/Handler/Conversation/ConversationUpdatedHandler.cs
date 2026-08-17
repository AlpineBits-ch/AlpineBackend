using Echo.Realtime;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Conversation;

public class ConversationUpdatedHandler
{
    public static async Task Handle(ConversationUpdated @event, MicroserviceContext ctx,
        IHubContext<EchoRealtimeHub> hubContext)
    {
        var memberIds = await ctx.Members
            .Where(m => m.ConversationId == @event.ConversationId)
            .Select(m => m.UserId)
            .AsNoTracking()
            .ToListAsync();

        await hubContext.Clients.Users(memberIds).SendAsync("conversation.ConversationUpdated", @event);
    }
}
