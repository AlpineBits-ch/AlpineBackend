using Federation.Contracts.Materialization.Messaging;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Wolverine;

namespace Messaging.Application.Bus.Federation;

/// <summary>Materializes remote channel-message federation events.</summary>
public class MessagingMaterializationHandlers
{
    public static async Task Handle(FederatedMessageCreatedReceived message, IMessageRepository repository, IMessageBus bus, CancellationToken ct)
    {
        var existing = await repository.GetMessageAsync(message.MessageId);
        if (existing is not null) return;

        var created = new Message
        {
            Id = message.MessageId,
            ContextId = message.ChannelId,
            ChannelId = message.ChannelId,
            AuthorId = message.SenderId,
            Content = message.Content,
            EncryptionState = MessageEncryptionState.Plain,
            Type = MessageType.Message,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.CreateMessageAsync(created);

        await bus.PublishAsync(new MessageCreated
        {
            MessageId = message.MessageId,
            ChannelId = message.ChannelId,
            AuthorId = message.SenderId,
            Content = message.Content,
            // Off the row that was just written, so the federated path denormalizes the same
            // timestamp downstream as the local one does.
            CreatedAt = created.CreatedAt,
            Attachments = [],
        });
    }

    public static async Task Handle(FederatedMessageEditedReceived message, IMessageRepository repository, IMessageBus bus, CancellationToken ct)
    {
        var existing = await repository.GetMessageAsync(message.MessageId);
        if (existing is null) return;

        existing.Content = message.Content;
        existing.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateMessageAsync(existing);

        await bus.PublishAsync(new MessageUpdated
        {
            MessageId = message.MessageId,
            ChannelId = message.ChannelId,
            AuthorId = existing.AuthorId,
            Content = message.Content,
        });
    }

    public static async Task Handle(FederatedMessageDeletedReceived message, IMessageRepository repository, IMessageBus bus, CancellationToken ct)
    {
        var existing = await repository.GetMessageAsync(message.MessageId);
        if (existing is null) return;

        await repository.DeleteMessageAsync(existing);

        await bus.PublishAsync(new MessageDeleted
        {
            MessageId = message.MessageId,
            ChannelId = message.ChannelId,
            AuthorId = existing.AuthorId,
        });
    }
}
