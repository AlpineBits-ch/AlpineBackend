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
        // message that carried the component. The caller has already established that the message
        // belongs to the bot handling the interaction, which the author comparison below cannot
        // see - RequestingAuthorId there is the human who clicked, not the message's author.
        // AuthorizationAlreadyChecked is the moderator/unfurler path: suppressing a preview with
        // DeleteAnyMessage, or the unfurler attaching one on nobody's behalf. Deliberately a second
        // flag rather than widening AllowBotAuthorEdit - that one asserts "this bot owns the
        // message", and folding a moderator bypass into it would hand every component interaction
        // the right to edit messages it does not own.
        if (!command.AllowBotAuthorEdit
            && !command.AuthorizationAlreadyChecked
            && message.AuthorId != command.RequestingAuthorId)
        {
            return (new UpdateMessageResponse { Forbidden = true }, null);
        }

        // Every field here is a patch: null means "the caller said nothing about this", a value
        // means "replace it". Content and EmbedsJson used to be assigned unconditionally, so an
        // ordinary text edit - which carries neither embeds nor components - overwrote the stored
        // embeds with null and destroyed them. That is not recoverable from history, and every
        // downstream notification faithfully reported the loss because the event below is built
        // from this same entity.
        // Optimistic-concurrency check for writes that were computed from a snapshot of this
        // message taken some time ago - in practice, an unfurl that spent seconds fetching a
        // third-party page. If the author edited the text in the meantime the preview describes a
        // link that is no longer in the message, so the correct outcome is to drop it silently.
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
        // generated ones and leave author-written cards in place. Done here, against the row this
        // handler is about to write, because a caller doing read-merge-write would silently revert
        // any author edit that landed between its read and its write.
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
        // from. Attaching or suppressing a link preview rewrites the row - so UpdatedAt moves - but
        // nobody edited anything, and labelling the message otherwise would be a lie the author
        // cannot correct.
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
