using Echo.Realtime;

using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Messages;

public class MessageUpdatedHandler
{
    public async Task Handle(MessageUpdated messageUpdated, IHubContext<EchoRealtimeHub> hubContext,
        MicroserviceContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(messageUpdated.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messageUpdated.ConversationId && m.UserId != messageUpdated.AuthorId).AsNoTracking().ToListAsync();
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("MessageUpdated", messageUpdated);
        }
    }

}