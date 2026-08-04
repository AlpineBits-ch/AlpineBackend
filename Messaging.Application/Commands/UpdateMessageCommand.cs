using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;
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
        // message that carried the component.
        if (!command.AllowBotAuthorEdit
            && !command.AuthorizationAlreadyChecked
            && message.AuthorId != command.RequestingAuthorId)
        {
            return (new UpdateMessageResponse { Forbidden = true }, null);
        }

        // Every field here is a patch: null means "the caller said nothing about this", a value
        // means "replace it".
        if (command.ExpectedContentSha256 is not null
            && !string.Equals(ContentHash.Of(message.Content), command.ExpectedContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return (new UpdateMessageResponse { Stale = true }, null);
        }

        if (command.Content is not null) message.Content = command.Content;
        // Null means "leave the embeds alone"; an empty array clears them.
        if (command.EmbedsJson is not null) message.EmbedsJson = command.EmbedsJson;

        // Merge semantics, unlike EmbedsJson's replace: generated previews swap out the previously
        // generated ones and leave author-written cards in place.
        if (command.GeneratedEmbedsJson is not null)
        {
            // A message whose previews were dismissed must not have them quietly restored by an
            // unfurl that was already in flight when the dismissal happened.
            if (!MessageFlags.Has(message.Flags, MessageFlags.SuppressEmbeds))
            {
                message.EmbedsJson = GeneratedEmbeds.Merge(
                    message.EmbedsJson, GeneratedEmbeds.Parse(command.GeneratedEmbedsJson));
            }
        }
        // Null means "leave the components alone"; an empty array clears them.
        if (command.ComponentsJson is not null) message.ComponentsJson = command.ComponentsJson;
        // Same patch rule again: null leaves the stored bitfield alone.
        if (command.Flags is not null) message.Flags = command.Flags.Value;

        var now = DateTime.UtcNow;
        message.UpdatedAt = now;

        // Only an author's own text edit moves EditedAt, which is what clients render "(edited)"
        // from.
        if (command.IsAuthorEdit && command.Content is not null) message.EditedAt = now;

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
            ComponentsJson = message.ComponentsJson,
            Flags = message.Flags,
            UpdatedAt = message.UpdatedAt,
            EditedAt = message.EditedAt,
            IsAuthorEdit = command.IsAuthorEdit,
        };

        return (response, messageUpdated);
    }
}
