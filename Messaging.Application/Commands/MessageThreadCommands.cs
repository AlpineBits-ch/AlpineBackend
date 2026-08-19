using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;

namespace Messaging.Application.Commands;

public class AttachThreadToMessageCommandHandler
{
    /// <summary>
    /// Points a message at the thread Guild created from it, refusing anything that would make the
    /// pointer a lie: an unknown message, one in a different channel, or one that already has a
    /// thread.
    /// </summary>
    public async Task<AttachThreadToMessageResponse> Handle(
        AttachThreadToMessageCommand command,
        IMessageRepository repo,
        // Present so AutoApplyTransactions sees a DbContext on this chain and commits what the
        // Postgres-backed repository tracked; the Scylla one writes directly and ignores it.
        // Deliberately not used directly here.
        MicroserviceContext db)
    {
        var message = await repo.GetMessageAsync(command.MessageId);
        if (message is null)
            return new AttachThreadToMessageResponse { Outcome = AttachThreadOutcome.MessageNotFound };

        // A message in a conversation has a null ChannelId, which fails this the same way a message
        // in the wrong channel does - threads are a guild-channel feature and a DM has no parent to
        // hang one under.
        if (!string.Equals(message.ChannelId, command.ChannelId, StringComparison.Ordinal))
            return new AttachThreadToMessageResponse { Outcome = AttachThreadOutcome.WrongChannel };

        if (!string.IsNullOrEmpty(message.ThreadId))
        {
            // Replaying the same attach is a success, not a conflict: Wolverine's inbox is
            // at-least-once and the caller has no way to tell a retry from a second request.
            if (string.Equals(message.ThreadId, command.ThreadId, StringComparison.Ordinal))
                return new AttachThreadToMessageResponse { Outcome = AttachThreadOutcome.Attached };

            return new AttachThreadToMessageResponse
            {
                Outcome = AttachThreadOutcome.AlreadyHasThread,
                ExistingThreadId = message.ThreadId,
            };
        }

        message.ThreadId = command.ThreadId;
        await repo.UpdateMessageAsync(message);

        return new AttachThreadToMessageResponse { Outcome = AttachThreadOutcome.Attached };
    }
}

public class DetachThreadFromMessageCommandHandler
{
    /// <summary>Clears the pointer once the thread is gone, and only while it still names that
    /// thread.</summary>
    public async Task Handle(
        DetachThreadFromMessageCommand command,
        IMessageRepository repo,
        // See AttachThreadToMessageCommandHandler.
        MicroserviceContext db)
    {
        var message = await repo.GetMessageAsync(command.MessageId);
        if (message is null) return;

        if (!string.Equals(message.ThreadId, command.ThreadId, StringComparison.Ordinal)) return;

        message.ThreadId = null;
        await repo.UpdateMessageAsync(message);
    }
}
