using Federation.Contracts.Materialization.Messaging;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Wolverine;

namespace Messaging.Application.Bus.Federation;

/// <summary>
/// Materializes remote channel-message federation events. Writes go through IMessageRepository
/// (the same abstraction CreateMessageCommandHandler uses) and then re-publish the local
/// Messaging.Domain.Events.Message.* domain events a normal message create/edit/delete already
/// raises - that's what MessageCreatedHandler/MessageUpdatedHandler/MessageDeletedHandler (in
/// Messaging.Application/Handler/Messages) turn into the realtime hub push, bots notification,
/// and Guild.Contracts.Bus.Events.MessageCreatedForChannel republish, so federated messages get
/// the exact same downstream treatment as local ones for free.
///
/// AuthorId is kept in federated (&lt;id&gt;:&lt;domain&gt;) form on the materialized Message,
/// not stripped like the other ids - this is what lets
/// Federation.Application.Bus.Outbound.MessagingOutboundHandlers recognize
/// (AuthorId.Contains(':')) and skip re-federating content that just arrived via federation,
/// which would otherwise echo it straight back out to the instance it came from.
///
/// Idempotent via IMessageRepository.GetMessageAsync before insert (EventId isn't used as the
/// key - Scylla has no unique-constraint-triggered upsert semantics to lean on the way Postgres
/// does, so the check has to happen explicitly).
/// </summary>
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
            // True: the (remote) author changed the text. This is also what re-queues the unfurl
            // for the new content - see UnfurlLinksHandler.
            IsAuthorEdit = true,
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
