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

        if (message.AuthorId != command.RequestingAuthorId)
        {
            return (new UpdateMessageResponse { Forbidden = true }, null);
        }

        message.Content = command.Content;
        message.EmbedsJson = command.EmbedsJson;
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
