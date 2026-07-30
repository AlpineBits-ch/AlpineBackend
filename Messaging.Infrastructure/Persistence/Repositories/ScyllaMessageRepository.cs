using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;


namespace Messaging.Infrastructure.Persistence.Repositories;

public class ScyllaMessageRepository(ScyllaContext context) : IMessageRepository
{
    public async Task<Message> CreateMessageAsync(Message message)
    {
        await context.Mapper.InsertAsync(message);
        return message;
    }

    public async Task<Message?> GetMessageAsync(string messageId)
    {
        var message = await context.Mapper.FirstOrDefaultAsync<Message>("WHERE message_id = ?", messageId);
        return message;
    }

 

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByConversationIdAsync(string conversationId, int take, int skip)
    {
        // messages' PRIMARY KEY is (context_id, created_at, message_id) - context_id is the
        // partition key. conversation_id/channel_id are denormalized metadata columns, not part
        // of the key, so filtering on them requires ALLOW FILTERING (a full partition scan) and
        // Scylla rejects it outright. Message.Create sets ContextId = ConversationId ?? ChannelId,
        // so querying by context_id with the conversation id is the actual indexed lookup.
        return GetMessagePageAsync(conversationId, take, skip);
    }

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByContextIdAsync(string contextId, int take, int skip)
        => GetMessagePageAsync(contextId, take, skip);

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByChannelIdAsync(string channelId, int take, int skip)
    {
        // Same partition-key lookup as the conversation variant - channel_id isn't part of
        // messages' PRIMARY KEY either, and Message.Create sets ContextId = ChannelId for
        // channel-scoped messages, so context_id is the correct indexed lookup for both.
        return GetMessagePageAsync(channelId, take, skip);
    }

    /// <summary>
    /// Reads one page of a message partition, newest-first off the wire, returned oldest-first.
    /// All three public list overloads funnel through here: conversation ids, channel ids and raw
    /// context ids are all just the messages partition key (see Message.Create).
    /// </summary>
    private async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageAsync(
        string contextId, int take, int skip)
    {
        if (take <= 0) return (new List<Message>(), new Dictionary<string, List<Reaction>>());
        if (skip < 0) skip = 0;

        var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";

        // ToList() here is load-bearing, not a style choice: Mapper.FetchAsync returns a lazy
        // projection over the driver's RowSet, whose enumerator *dequeues* from an internal
        // ConcurrentQueue<Row>. It is a single-pass, self-consuming sequence - enumerating it a
        // second time yields nothing. Materializing once up front is the only safe way to read it
        // more than once.
        var messageItems = (await context.Mapper.FetchAsync<Message>(cql, contextId, skip + take)).ToList();

        var result = messageItems
            .Skip(skip)
            .Take(take)
            .OrderBy(m => m.CreatedAt) // Flip them back to chronological order
            .ToList();

        var reactionCql = "SELECT * FROM reactions WHERE context_id = ? AND message_id = ?";
        var reactionTasks = result.Select(m =>
            context.Mapper.FetchAsync<Reaction>(reactionCql, m.ContextId, m.Id));

        var reactionResults = await Task.WhenAll(reactionTasks);

        var reactionsByMessage = result
            .Zip(reactionResults, (m, reactions) => (m.Id, Reactions: reactions.ToList()))
            .ToDictionary(x => x.Id, x => x.Reactions);

        // Return the paged/ordered page - not the raw fetch, which ignores skip, comes back
        // newest-first, and has no matching entries in reactionsByMessage.
        return (result, reactionsByMessage);
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageByCursorAsync(
        MessagePageQuery query)
    {
        var empty = ((ICollection<Message>)new List<Message>(), new Dictionary<string, List<Reaction>>());
        if (query.Limit <= 0) return empty;

        // The cursor is an id, but the clustering key is (created_at, message_id) - so the anchor
        // has to be resolved to its actual sort position first. This rides the secondary index on
        // message_id (see ScyllaContext.RunMigrationsAsync).
        var anchor = await GetMessageAsync(query.AnchorMessageId);
        if (anchor is null || anchor.ContextId != query.ContextId) return empty;

        List<Message> page;
        if (query.Direction == MessageCursorDirection.Around)
        {
            // Split either side of the anchor. The anchor itself is returned too, so the two
            // halves are sized to leave room for it rather than each taking the full limit.
            var half = Math.Max(1, (query.Limit - 1) / 2);
            var older = await FetchRelativeAsync(query.ContextId, anchor, before: true, limit: half);
            var newer = await FetchRelativeAsync(query.ContextId, anchor, before: false, limit: half);

            page = [.. older, anchor, .. newer];
        }
        else
        {
            page = await FetchRelativeAsync(query.ContextId, anchor,
                before: query.Direction == MessageCursorDirection.Before, limit: query.Limit);
        }

        var ordered = page.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToList();
        return (ordered, await FetchReactionsAsync(ordered));
    }

    /// <summary>One side of a cursor page.
    ///
    /// The comparison is the CQL row-tuple form <c>(created_at, message_id) &lt; (?, ?)</c> rather
    /// than <c>created_at &lt; ?</c>, because created_at is not unique - two messages posted in the
    /// same millisecond are ordered by message_id, the third clustering column. Comparing on the
    /// timestamp alone would re-emit every same-millisecond sibling of the anchor on the next page
    /// (or skip them all, going the other way), which is exactly the bug that shows up as
    /// "duplicate message while scrolling" under load and never in testing.</summary>
    private async Task<List<Message>> FetchRelativeAsync(string contextId, Message anchor, bool before, int limit)
    {
        // ORDER BY here is the *read* order, which for `after` is the reverse of the table's
        // declared clustering order - legal within a single partition, and necessary so that LIMIT
        // takes the rows adjacent to the anchor rather than the newest in the partition.
        var comparison = before ? "<" : ">";
        var order = before ? "DESC" : "ASC";

        var cql =
            $"SELECT {Message.SelectColumns} FROM messages " +
            $"WHERE context_id = ? AND (created_at, message_id) {comparison} (?, ?) " +
            $"ORDER BY created_at {order} LIMIT ?";

        // Materialized immediately: Mapper.FetchAsync hands back a single-pass projection over the
        // driver's RowSet, which self-consumes on enumeration (see GetMessagePageAsync's comment).
        return (await context.Mapper.FetchAsync<Message>(cql, contextId, anchor.CreatedAt, anchor.Id, limit)).ToList();
    }

    private async Task<Dictionary<string, List<Reaction>>> FetchReactionsAsync(List<Message> messages)
    {
        const string reactionCql = "SELECT * FROM reactions WHERE context_id = ? AND message_id = ?";
        var reactionResults = await Task.WhenAll(messages.Select(m =>
            context.Mapper.FetchAsync<Reaction>(reactionCql, m.ContextId, m.Id)));

        return messages
            .Zip(reactionResults, (m, reactions) => (m.Id, Reactions: reactions.ToList()))
            .ToDictionary(x => x.Id, x => x.Reactions);
    }

    public async Task<Message> UpdateMessageAsync(Message message)
    {
        await context.Mapper.UpdateAsync(message);
        return message;
    }

    public async Task DeleteMessageAsync(Message message)
    {
        await context.Mapper.DeleteAsync(message);
    }

    public async Task DeleteMessagesAsync(IReadOnlyCollection<Message> messages)
    {
        // Issued one at a time rather than as a CQL batch. A bulk delete is always scoped to a
        // single channel, so these do share a partition and would form a legal single-partition
        // batch - but the caller already caps the set at 100 rows, which is small enough that the
        // batch's only real benefit (one coordinator round trip) does not pay for the extra
        // IMapper surface every test fake would then have to implement. Sequential rather than
        // Task.WhenAll so a 100-row delete cannot open 100 concurrent driver operations.
        foreach (var message in messages)
        {
            await context.Mapper.DeleteAsync(message);
        }
    }

    public async Task<Message> PinMessageAsync(Message message, string pinnedById)
    {
        message.IsPinned = true;
        message.PinnedAt = DateTime.UtcNow;
        message.PinnedById = pinnedById;
        await context.Mapper.UpdateAsync(message);

        await context.Mapper.InsertAsync(new PinnedMessage
        {
            ContextId = message.ContextId,
            MessageId = message.Id,
            PinnedAt = message.PinnedAt.Value,
            PinnedById = pinnedById,
        });

        return message;
    }

    public async Task<Message> UnpinMessageAsync(Message message)
    {
        var pinnedAt = message.PinnedAt;
        message.IsPinned = false;
        message.PinnedAt = null;
        message.PinnedById = null;
        await context.Mapper.UpdateAsync(message);

        if (pinnedAt is not null)
        {
            await context.Mapper.DeleteAsync<PinnedMessage>(
                "WHERE context_id = ? AND pinned_at = ? AND message_id = ?",
                message.ContextId, pinnedAt.Value, message.Id);
        }

        return message;
    }

    public async Task<ICollection<Message>> GetPinnedMessagesAsync(string contextId, int limit = 50)
    {
        var pins = await context.Mapper.FetchAsync<PinnedMessage>(
            "WHERE context_id = ? LIMIT ?", contextId, limit);

        var messageTasks = pins.Select(p => GetMessageAsync(p.MessageId));
        var messages = await Task.WhenAll(messageTasks);

        return messages.Where(m => m is not null).ToList()!;
    }

    public async Task AddReactionAsync(Reaction reaction)
    {
        await context.Mapper.InsertAsync(reaction);
    }

    public async Task RemoveReactionAsync(string contextId, string messageId, string emoji, string userId)
    {
        await context.Mapper.DeleteAsync<Reaction>(
            "WHERE context_id = ? AND message_id = ? AND emoji = ? AND user_id = ?",
            contextId, messageId, emoji, userId);
    }
}