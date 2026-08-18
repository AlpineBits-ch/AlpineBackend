using Echo.Realtime;

using Guild.Contracts.Bus.Events;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using DomainAuthorIdType = Messaging.Domain.Enums.AuthorIdType;
using ChannelAuthorIdType = Guild.Contracts.Bus.Events.AuthorIdType;

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
                // Same reason as on the created event: clients re-render from this payload, so
                // omitting the overrides would drop the character off an edited message.
                AuthorIdType = messageUpdated.AuthorIdType switch
                {
                    DomainAuthorIdType.Bot => ChannelAuthorIdType.Bot,
                    DomainAuthorIdType.Webhook => ChannelAuthorIdType.Webhook,
                    DomainAuthorIdType.Persona => ChannelAuthorIdType.Persona,
                    _ => ChannelAuthorIdType.User,
                },
                AuthorDisplayName = messageUpdated.AuthorDisplayName,
                AuthorAvatarUrl = messageUpdated.AuthorAvatarUrl,
                PersonaId = messageUpdated.PersonaId,
            });
        }

        // Re-unfurl when the author changed the text, so editing a link changes the card.
        if (UnfurlDecision.ShouldUnfurlEdit(
                messageUpdated.IsAuthorEdit, messageUpdated.Flags, messageUpdated.Content))
        {
            // Either id resolves to the same storage partition, which is all the worker needs.
            var contextId = messageUpdated.ConversationId ?? messageUpdated.ChannelId;
            if (!string.IsNullOrWhiteSpace(contextId))
            {
                await bus.PublishAsync(new UnfurlMessageLinks
                {
                    MessageId = messageUpdated.MessageId,
                    ContextId = contextId,
                });
            }
        }
    }

}