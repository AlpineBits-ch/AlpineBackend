using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Writes the mention-index rows for one chunk of recipients.
///
/// Idempotent by primary key on both backends, which is not incidental: Wolverine retries this, and
/// the Mentions tab counts these rows, so a replay that doubled them would double the badge.
/// </summary>
public class IndexMentionsHandler
{
    public static async Task Handle(IndexMentionsCommand command, IMentionIndexRepository repo)
    {
        if (command.Recipients.Count == 0) return;

        var rows = command.Recipients
            .DistinctBy(r => r.UserId, StringComparer.Ordinal)
            .Where(r => !string.Equals(r.UserId, command.AuthorId, StringComparison.Ordinal))
            .Select(r => new UserMention
            {
                UserId = r.UserId,
                CreatedAt = command.CreatedAt,
                MessageId = command.MessageId,
                ContextId = command.ContextId,
                GuildId = command.GuildId,
                ChannelId = command.ChannelId,
                ConversationId = command.ConversationId,
                AuthorId = command.AuthorId,
                Kind = r.Kind,
            })
            .ToList();

        await repo.AddAsync(rows);
    }
}
