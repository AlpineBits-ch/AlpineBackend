using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Repositories;
using MessagePinned = Messaging.Domain.Events.Message.MessagePinned;
using MessageUnpinned = Messaging.Domain.Events.Message.MessageUnpinned;

namespace Messaging.Application.Commands;

public class PinMessageCommandHandler
{
    public async Task<(PinMessageResponse, MessagePinned?)> Handle(PinMessageCommand command, IMessageRepository repo)
    {
        var message = await repo.GetMessageAsync(command.MessageId);
        if (message is null)
        {
            return (new PinMessageResponse { NotFound = true }, null);
        }

        if (message.IsPinned)
        {
            return (new PinMessageResponse
            {
                Success = true,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                AuthorId = message.AuthorId,
                PinnedById = message.PinnedById,
                PinnedAt = message.PinnedAt,
            }, null);
        }

        await repo.PinMessageAsync(message, command.RequestingUserId);

        var response = new PinMessageResponse
        {
            Success = true,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
            PinnedById = message.PinnedById,
            PinnedAt = message.PinnedAt,
        };

        var pinned = new MessagePinned
        {
            MessageId = message.Id,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
            PinnedById = command.RequestingUserId,
            PinnedAt = message.PinnedAt!.Value,
        };

        return (response, pinned);
    }
}

public class UnpinMessageCommandHandler
{
    public async Task<(PinMessageResponse, MessageUnpinned?)> Handle(UnpinMessageCommand command, IMessageRepository repo)
    {
        var message = await repo.GetMessageAsync(command.MessageId);
        if (message is null)
        {
            return (new PinMessageResponse { NotFound = true }, null);
        }

        if (!message.IsPinned)
        {
            return (new PinMessageResponse
            {
                Success = true,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                AuthorId = message.AuthorId,
            }, null);
        }

        await repo.UnpinMessageAsync(message);

        var response = new PinMessageResponse
        {
            Success = true,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
        };

        var unpinned = new MessageUnpinned
        {
            MessageId = message.Id,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
            UnpinnedById = command.RequestingUserId,
        };

        return (response, unpinned);
    }
}
