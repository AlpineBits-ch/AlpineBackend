using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Handler.Messages;

public class MessageDeletedHandler
{
    public async Task Handle(MessageDeleted messageDeleted, IHubContext<EchoRealtimeHub> hubContext,
        MicroserviceContext ctx, IMessageBus bus)
    {
        await ctx.MessageSearchEntries.Where(e => e.MessageId == messageDeleted.MessageId).ExecuteDeleteAsync();

        if (!string.IsNullOrWhiteSpace(messageDeleted.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messageDeleted.ConversationId && m.UserId != messageDeleted.AuthorId).AsNoTracking().ToListAsync();
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MessageDeleted", messageDeleted);
        }

        if (!string.IsNullOrWhiteSpace(messageDeleted.ChannelId))
        {
            await bus.SendAsync(new MessageDeletedForChannel
            {
                ChannelId = messageDeleted.ChannelId,
                MessageId = messageDeleted.MessageId,
                AuthorId = messageDeleted.AuthorId,
            });
        }
    }

}