using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Repositories;

namespace Messaging.Application.Handler.Messages;

/// <summary>Serves <see cref="GetMessageRequest"/> - the read Bots needs to validate a component
/// interaction against the message that supposedly carried the component.</summary>
public class GetMessageHandler
{
    public static async Task<GetMessageResponse> Handle(GetMessageRequest request, IMessageRepository repo)
    {
        var message = await repo.GetMessageAsync(request.MessageId);

        return new GetMessageResponse
        {
            Message = message is null ? null : new MessageSummary
            {
                Id = message.Id,
                AuthorId = message.AuthorId,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                Content = message.Content,
                ComponentsJson = message.ComponentsJson,
                EmbedsJson = message.EmbedsJson,
            },
        };
    }
}
