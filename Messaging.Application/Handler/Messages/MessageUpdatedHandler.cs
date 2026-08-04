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
        // Only messages that were originally indexed (Plain-encryption ordinary messages) have a
        // search entry to update - an encrypted message being edited still has nothing to index.
        var searchEntry = await ctx.MessageSearchEntries.FirstOrDefaultAsync(e => e.MessageId == messageUpdated.MessageId);
        if (searchEntry is not null)
        {
            searchEntry.Content = System.Text.Encoding.UTF8.GetString(messageUpdated.Content);
        }

        if (!string.IsNullOrWhiteSpace(messageUpdated.ConversationId))
        {
            // The author is excluded from their own edits - their client already rendered the
            // change it just made.
            var conversationMembers = await ctx.Members
                .Where(m => m.ConversationId == messageUpdated.ConversationId
                            && (!messageUpdated.IsAuthorEdit || m.UserId != messageUpdated.AuthorId))
                .AsNoTracking()
                .ToListAsync();

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
                ComponentsJson = messageUpdated.ComponentsJson,
                Flags = messageUpdated.Flags,
                EditedAt = messageUpdated.EditedAt,
                IsAuthorEdit = messageUpdated.IsAuthorEdit,
            });
        }
    }

}