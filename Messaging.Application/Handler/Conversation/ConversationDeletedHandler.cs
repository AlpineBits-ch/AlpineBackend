using Echo.Realtime;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Conversation;

public class ConversationDeletedHandler
{
    public static async Task Handle(ConversationDeleted conversationDeleted,
        MicroserviceContext ctx, IDistributedCache cache, IHubContext<EchoRealtimeHub> hubContext)
    {
        var conversationMembers = await ctx.Members.Where(m => m.ConversationId == conversationDeleted.ConversationId).AsNoTracking().ToListAsync();
        await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.ConversationDeleted", conversationDeleted);
    }
}