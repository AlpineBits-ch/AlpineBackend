using Messaging.Application.Hubs;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Conversation;

public class ConversationMemberLeftHandler
{
    public static async Task Handle(ConversationMemberRemoved memberRemoved,
        MicroserviceContext ctx, IDistributedCache cache, IHubContext<MessagingHub> hubContext)
    {
        var conversationMembers = await ctx.Members.Where(m => m.ConversationId == memberRemoved.ConversationId && m.UserId != memberRemoved.UserId).AsNoTracking().ToListAsync();
        await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("MemberLeft", memberRemoved);
    }
}