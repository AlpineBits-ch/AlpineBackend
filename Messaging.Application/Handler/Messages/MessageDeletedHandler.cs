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
        // Tracked-entity removal rather than ExecuteDeleteAsync, mirroring MessageUpdatedHandler's
        // approach to the same MessageSearchEntry side effect right below it - this Handle method
        // is bus-dispatched and auto-wrapped by Wolverine (see repo convention), so no manual
        // SaveChangesAsync here; Wolverine's middleware commits the Remove() after Handle returns.
        var searchEntry = await ctx.MessageSearchEntries.FirstOrDefaultAsync(e => e.MessageId == messageDeleted.MessageId);
        if (searchEntry is not null) ctx.MessageSearchEntries.Remove(searchEntry);

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