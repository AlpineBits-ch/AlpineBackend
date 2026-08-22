using Federation.Contracts.Materialization.Messaging;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Wolverine;

namespace Messaging.Application.Bus.Federation;

/// <summary>Materializes remote channel-message federation events.</summary>
public class MessagingMaterializationHandler
{
    /// <summary>
    /// A federated persona is display data, not an entity: the name and avatar that arrived are
    /// stored and rendered, and PersonaId stays null because a remote instance's persona row is not
    /// resolvable here. The wire contract carries no persona id at all, so there is nothing to
    /// assign by mistake.
    /// </summary>
    private static AuthorIdType MapAuthorIdType(FederatedAuthorIdType type) => type switch
    {
        FederatedAuthorIdType.Bot => AuthorIdType.Bot,
        FederatedAuthorIdType.Webhook => AuthorIdType.Webhook,
        FederatedAuthorIdType.Persona => AuthorIdType.Persona,
        _ => AuthorIdType.User,
    };

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
            AuthorDisplayName = message.AuthorDisplayName,
            AuthorAvatarUrl = message.AuthorAvatarUrl,
            AuthorIdType = MapAuthorIdType(message.AuthorIdType),
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
            // Same reason the timestamp comes off the row: without these the receiving instance's
            // own realtime fan-out and push render the raw federated id instead of the character.
            AuthorDisplayName = created.AuthorDisplayName,
            AuthorAvatarUrl = created.AuthorAvatarUrl,
            AuthorIdType = created.AuthorIdType,
            Attachments = [],
        });
    }

    public static async Task Handle(FederatedMessageEditedReceived message, IMessageRepository repository, IMessageBus bus, CancellationToken ct)
    {
        var existing = await repository.GetMessageAsync(message.MessageId);
        if (existing is null) return;

        existing.Content = message.Content;
        existing.UpdatedAt = DateTime.UtcNow;
        // A federated edit is the remote author editing their own text, so it moves EditedAt and
        // clients should show "(edited)" - unlike a local preview attachment, which does not.
        existing.EditedAt = existing.UpdatedAt;
        await repository.UpdateMessageAsync(existing);

        await bus.PublishAsync(new MessageUpdated
        {
            MessageId = message.MessageId,
            ChannelId = message.ChannelId,
            AuthorId = existing.AuthorId,
            Content = message.Content,
            // Off the stored row: a federated edit only ever carries text, and omitting this made
            // the update notification report no embeds on a message that still has them - the
            // clients that re-render from the payload would drop the card until a refetch.
            EmbedsJson = existing.EmbedsJson,
            ComponentsJson = existing.ComponentsJson,
            Flags = existing.Flags,
            UpdatedAt = existing.UpdatedAt,
            EditedAt = existing.EditedAt,
            // True: the (remote) author changed the text.
            IsAuthorEdit = true,
            // Off the stored row for the same reason as the embeds above - the edit carries no
            // identity, and dropping these would strip the character off a federated message.
            AuthorIdType = existing.AuthorIdType,
            AuthorDisplayName = existing.AuthorDisplayName,
            AuthorAvatarUrl = existing.AuthorAvatarUrl,
            PersonaId = existing.PersonaId,
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
