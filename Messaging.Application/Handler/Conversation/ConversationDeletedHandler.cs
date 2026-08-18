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

        // Drafts carry no foreign key to the conversation, so nothing cascades them away.
        var drafts = await ctx.MessageDrafts
            .Where(d => d.ContextId == conversationDeleted.ConversationId)
            .ToListAsync();
        ctx.MessageDrafts.RemoveRange(drafts);

        await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.ConversationDeleted", conversationDeleted);
    }
}