using Echo.Realtime;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Messages;

public class MessageDeletedHandler
{
    public async Task Handle(MessageDeleted messageDeleted, IHubContext<EchoRealtimeHub> hubContext,
        MicroserviceContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(messageDeleted.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messageDeleted.ConversationId && m.UserId != messageDeleted.AuthorId).AsNoTracking().ToListAsync();
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MessageDeleted", messageDeleted);
        }
    }

}