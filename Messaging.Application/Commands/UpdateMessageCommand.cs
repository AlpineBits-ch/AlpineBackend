using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;

namespace Messaging.Application.Commands;

public class UpdateMessageCommandHandler
{
    public async Task<(UpdateMessageResponse, MessageUpdated?)> Handle(UpdateMessageCommand command, IMessageRepository repo)
    {
        var message = await repo.GetMessageAsync(command.MessageId);
        if (message is null)
        {
            return (new UpdateMessageResponse { NotFound = true }, null);
        }

        // AllowBotAuthorEdit is the UPDATE_MESSAGE path: a component interaction editing the very
        // message that carried the component. The caller has already established that the message
        // belongs to the bot handling the interaction, which the author comparison below cannot
        // see - RequestingAuthorId there is the human who clicked, not the message's author.
        if (!command.AllowBotAuthorEdit && message.AuthorId != command.RequestingAuthorId)
        {
            return (new UpdateMessageResponse { Forbidden = true }, null);
        }

        // Every field here is a patch: null means "the caller said nothing about this", a value
        // means "replace it". Content and EmbedsJson used to be assigned unconditionally, so an
        // ordinary text edit - which carries neither embeds nor components - overwrote the stored
        // embeds with null and destroyed them. That is not recoverable from history, and every
        // downstream notification faithfully reported the loss because the event below is built
        // from this same entity.
        if (command.Content is not null) message.Content = command.Content;
        // Null means "leave the embeds alone"; an empty array clears them.
        if (command.EmbedsJson is not null) message.EmbedsJson = command.EmbedsJson;
        // Null means "leave the components alone"; an empty array clears them.
        if (command.ComponentsJson is not null) message.ComponentsJson = command.ComponentsJson;
        message.UpdatedAt = DateTime.UtcNow;
        await repo.UpdateMessageAsync(message);

        var response = new UpdateMessageResponse
        {
            Success = true,
            Content = message.Content,
            EmbedsJson = message.EmbedsJson,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
            UpdatedAt = message.UpdatedAt,
        };

        var messageUpdated = new MessageUpdated
        {
            MessageId = message.Id,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            Content = message.Content,
            AuthorId = message.AuthorId,
            EmbedsJson = message.EmbedsJson,
        };

        return (response, messageUpdated);
    }
}
