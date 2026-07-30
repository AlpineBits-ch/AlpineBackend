using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Handler.Messages;

public class MessagePinnedHandler
{
    public async Task Handle(MessagePinned messagePinned, IHubContext<EchoRealtimeHub> hubContext,
        MicroserviceContext ctx, IMessageBus bus)
    {
        if (!string.IsNullOrWhiteSpace(messagePinned.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messagePinned.ConversationId).AsNoTracking().ToListAsync();
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MessagePinned", messagePinned);
        }

        if (!string.IsNullOrWhiteSpace(messagePinned.ChannelId))
        {
            await bus.SendAsync(new MessagePinnedForChannel
            {
                ChannelId = messagePinned.ChannelId,
                MessageId = messagePinned.MessageId,
                AuthorId = messagePinned.AuthorId,
                PinnedById = messagePinned.PinnedById,
                PinnedAt = messagePinned.PinnedAt,
            });
        }
    }
}

public class MessageUnpinnedHandler
{
    public async Task Handle(MessageUnpinned messageUnpinned, IHubContext<EchoRealtimeHub> hubContext,
        MicroserviceContext ctx, IMessageBus bus)
    {
        if (!string.IsNullOrWhiteSpace(messageUnpinned.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messageUnpinned.ConversationId).AsNoTracking().ToListAsync();
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MessageUnpinned", messageUnpinned);
        }

        if (!string.IsNullOrWhiteSpace(messageUnpinned.ChannelId))
        {
            await bus.SendAsync(new MessageUnpinnedForChannel
            {
                ChannelId = messageUnpinned.ChannelId,
                MessageId = messageUnpinned.MessageId,
                AuthorId = messageUnpinned.AuthorId,
                UnpinnedById = messageUnpinned.UnpinnedById,
            });
        }
    }
}
