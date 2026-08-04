using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Infrastructure.Persistence.Repositories;

// Every mutating method here is called exclusively from Wolverine-managed code (bus handlers or
// WolverineHttp endpoints - see IMessageRepository's call sites), which auto-wraps the ambient
// MicroserviceContext in a transaction and commits it once the handler/endpoint returns
// (opts.Policies.AutoApplyTransactions() + UseEntityFrameworkCoreTransactions()). None of these
// methods call SaveChangesAsync themselves: doing so double-saves against the same DbContext
// instance Wolverine is already tracking for its own commit/outbox-flush, which is unsafe.
public class EfCoreMessageRepository(MicroserviceContext context) : IMessageRepository
{
    public Task<Message> CreateMessageAsync(Message message)
    {
        context.Messages.Add(message);
        return Task.FromResult(message);
    }

    public async Task<Message?> GetMessageAsync(string messageId)
    {
        return await context.Messages
            .AsNoTracking()
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId);
    }

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByConversationIdAsync(
        string conversationId, int take, int skip)
        => OffsetPageAsync(context.Messages.Where(m => m.ConversationId == conversationId), take, skip);

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByContextIdAsync(
        string contextId, int take, int skip)
        => OffsetPageAsync(context.Messages.Where(m => m.ContextId == contextId), take, skip);

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByChannelIdAsync(
        string channelId, int take, int skip)
        => OffsetPageAsync(context.Messages.Where(m => m.ChannelId == channelId), take, skip);

    /// <summary>
    /// One offset page: newest-first to apply the offset, returned oldest-first.
    ///
    /// <para><b>The ORDER BY names the id as well as the timestamp, and has to.</b> CreatedAt is not
    /// unique - two messages posted in the same millisecond share it - so ordering on it alone leaves
    /// their relative position up to the query plan, and <c>OFFSET</c> counting down an order that is
    /// not total can hand the same message to two pages or to neither. It also has to be a total
    /// order for the Scylla backend to be able to match it: that backend's clustering key is
    /// <c>(created_at, message_id)</c>, so <c>(CreatedAt, Id)</c> is the only ordering both can
    /// produce. Offset paging is still deprecated for new callers - it drifts as messages arrive
    /// underneath the reader, which no ORDER BY fixes - see <see cref="MessagePageQuery"/>.</para>
    /// </summary>
    private async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> OffsetPageAsync(
        IQueryable<Message> scoped, int take, int skip)
    {
        if (take <= 0) return (new List<Message>(), new Dictionary<string, List<Reaction>>());
        if (skip < 0) skip = 0;

        var messages = await scoped
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Skip(skip)
            .Take(take)
            .Include(m => m.Attachments)
            .ToListAsync();

        // Ordinal, matching the byte-wise comparison the Scylla backend's clustering key performs.
        messages = messages
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var reactionsByMessage = await FetchReactionsForMessages(messages);
        return (messages, reactionsByMessage);
    }

    public async Task<IReadOnlyList<Message>> GetContextMessagesOlderThanAsync(
        string contextId, DateTimeOffset olderThan, DateTimeOffset afterCreatedAt, string afterMessageId, int limit)
    {
        if (limit <= 0) return [];

        // The relational twin of the Scylla row-tuple comparison, spelled out longhand because EF
        // Core cannot translate a ValueTuple comparison into SQL's row-value syntax - same reason
        // and same shape as RelativePageAsync above.
        return await context.Messages
            .AsNoTracking()
            .Where(m => m.ContextId == contextId
                        && m.CreatedAt < olderThan
                        && (m.CreatedAt > afterCreatedAt
                            || (m.CreatedAt == afterCreatedAt && string.Compare(m.Id, afterMessageId) > 0)))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Take(limit)
            .Include(m => m.Attachments)
            .ToListAsync();
    }

    public Task<Message> UpdateMessageAsync(Message message)
    {
        context.Messages.Update(message);
        return Task.FromResult(message);
    }

    public Task DeleteMessageAsync(Message message)
    {
        context.Messages.Remove(message);
        return Task.CompletedTask;
    }

    public Task DeleteMessagesAsync(IReadOnlyCollection<Message> messages)
    {
        context.Messages.RemoveRange(messages);
        return Task.CompletedTask;
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageByCursorAsync(
        MessagePageQuery query)
    {
        var empty = ((ICollection<Message>)new List<Message>(), new Dictionary<string, List<Reaction>>());
        if (query.Limit <= 0) return empty;

        var anchor = await context.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == query.AnchorMessageId && m.ContextId == query.ContextId);
        if (anchor is null) return empty;

        List<Message> page;
        if (query.Direction == MessageCursorDirection.Around)
        {
            var half = Math.Max(1, (query.Limit - 1) / 2);
            var older = await RelativePageAsync(query.ContextId, anchor, before: true, limit: half);
            var newer = await RelativePageAsync(query.ContextId, anchor, before: false, limit: half);
            page = [.. older, anchor, .. newer];
        }
        else
        {
            page = await RelativePageAsync(query.ContextId, anchor,
                before: query.Direction == MessageCursorDirection.Before, limit: query.Limit);
        }

        // Ordinal, not the culture-sensitive default: the Scylla backend's clustering key compares
        // message ids byte-wise, and a client that pages one backend and then the other must not see
        // two orders. Message ids are `mesg_` plus a 26-character Crockford base32 ULID body
        // (see Ids.Identifier), so the two comparisons agree on every id this actually sorts - the
        // ordinal spelling is what makes that true by construction rather than by luck.
        var ordered = page
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        return (ordered, await FetchReactionsForMessages(ordered));
    }

    /// <summary>The relational twin of ScyllaMessageRepository.FetchRelativeAsync - and it has to
    /// break the (created_at, message_id) tuple comparison out longhand, because EF Core cannot
    /// translate a ValueTuple comparison into SQL's row-value syntax. Same semantics, same reason:
    /// created_at is not unique, so ordering and paging both have to fall through to the id.</summary>
    private async Task<List<Message>> RelativePageAsync(string contextId, Message anchor, bool before, int limit)
    {
        var scoped = context.Messages.AsNoTracking().Where(m => m.ContextId == contextId);

        var filtered = before
            ? scoped.Where(m => m.CreatedAt < anchor.CreatedAt
                             || (m.CreatedAt == anchor.CreatedAt && string.Compare(m.Id, anchor.Id) < 0))
            : scoped.Where(m => m.CreatedAt > anchor.CreatedAt
                             || (m.CreatedAt == anchor.CreatedAt && string.Compare(m.Id, anchor.Id) > 0));

        // Ordered toward the anchor so LIMIT takes the adjacent rows, then flipped by the caller.
        var ordered = before
            ? filtered.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            : filtered.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id);

        return await ordered.Take(limit).Include(m => m.Attachments).ToListAsync();
    }

    public Task<Message> PinMessageAsync(Message message, string pinnedById)
    {
        message.IsPinned = true;
        message.PinnedAt = DateTime.UtcNow;
        message.PinnedById = pinnedById;
        context.Messages.Update(message);
        return Task.FromResult(message);
    }

    public Task<Message> UnpinMessageAsync(Message message)
    {
        message.IsPinned = false;
        message.PinnedAt = null;
        message.PinnedById = null;
        context.Messages.Update(message);
        return Task.FromResult(message);
    }

    public async Task<ICollection<Message>> GetPinnedMessagesAsync(string contextId, int limit = 50)
    {
        return await context.Messages
            .AsNoTracking()
            .Where(m => m.ContextId == contextId && m.IsPinned)
            .OrderByDescending(m => m.PinnedAt)
            .Take(limit)
            .Include(m => m.Attachments)
            .ToListAsync();
    }

    public Task AddReactionAsync(Reaction reaction)
    {
        context.Reactions.Add(reaction);
        return Task.CompletedTask;
    }

    public async Task RemoveReactionAsync(string contextId, string messageId, string emoji, string userId)
    {
        // Fetch-then-remove rather than ExecuteDeleteAsync: (MessageId, UserId, Emoji) is the
        // Reaction primary key (see MicroserviceContext.OnModelCreating), so at most one row ever
        // matches, and ExecuteDeleteAsync isn't translatable by the EF Core InMemory provider used
        // by this test suite. No SaveChangesAsync - see class-level comment.
        var reaction = await context.Reactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.Emoji == emoji && r.UserId == userId);

        if (reaction is null) return;

        context.Reactions.Remove(reaction);
    }

    private async Task<Dictionary<string, List<Reaction>>> FetchReactionsForMessages(List<Message> messages)
    {
        var messageIds = messages.Select(m => m.Id).ToList();
        var reactions = await context
            .Reactions
            .AsNoTracking()
            .Where(r => messageIds.Contains(r.MessageId))
            .ToListAsync();

        return messages.ToDictionary(
            m => m.Id,
            m => reactions.Where(r => r.MessageId == m.Id).ToList()
        );
    }
}
