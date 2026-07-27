using Echo.Realtime;

using Guild.Contracts.Bus.Events;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Handler.Messages;

public class MessageUpdatedHandler
{
    public async Task Handle(MessageUpdated messageUpdated, IHubContext<EchoRealtimeHub> hubContext,
        MicroserviceContext ctx, IMessageBus bus)
    {
        if (!string.IsNullOrWhiteSpace(messageUpdated.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messageUpdated.ConversationId && m.UserId != messageUpdated.AuthorId).AsNoTracking().ToListAsync();
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MessageUpdated", messageUpdated);
        }

        if (!string.IsNullOrWhiteSpace(messageUpdated.ChannelId))
        {
            await bus.SendAsync(new MessageUpdatedForChannel
            {
                ChannelId = messageUpdated.ChannelId,
                MessageId = messageUpdated.MessageId,
                AuthorId = messageUpdated.AuthorId,
                Content = messageUpdated.Content,
                EmbedsJson = messageUpdated.EmbedsJson,
            });
        }
    }

}