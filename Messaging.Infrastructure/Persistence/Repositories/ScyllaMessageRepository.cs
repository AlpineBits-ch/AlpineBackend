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
        // partition key. conversation_id/channel_id are denormalized metadata columns, not part of
        // the key, so filtering on them requires ALLOW FILTERING (a full partition scan) and Scylla
        // rejects it outright.
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
    /// </summary>
    private async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageAsync(
        string contextId, int take, int skip)
    {
        if (take <= 0) return (new List<Message>(), new Dictionary<string, List<Reaction>>());
        if (skip < 0) skip = 0;

        var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";
        var wanted = skip + take;

        // ToList() here is load-bearing, not a style choice: Mapper.FetchAsync returns a lazy
        // projection over the driver's RowSet, whose enumerator dequeues from an internal
        // ConcurrentQueue<Row>.
        var messageItems = (await context.Mapper.FetchAsync<Message>(cql, contextId, wanted)).ToList();

        // A short read exhausted the partition, so its last millisecond is already whole.
        if (messageItems.Count >= wanted)
        {
            var boundary = messageItems[^1].CreatedAt;
            var wholeGroup = await ReadInstantAsync(contextId, boundary);
            messageItems = messageItems.Where(m => m.CreatedAt != boundary).Concat(wholeGroup).ToList();
        }

        var result = messageItems
            // Newest-first with the tie-break spelled out, so `skip` counts down a total order
            // rather than down whatever the scan happened to emit.
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id, StringComparer.Ordinal)
            .Skip(skip)
            .Take(take)
            // Flip them back to chronological order - the same (created_at, message_id) ascending
            // order every other read here returns, and the one the relational backend returns.
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
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
        // has to be resolved to its actual sort position first.
        var anchor = await GetMessageAsync(query.AnchorMessageId);
        if (anchor is null || anchor.ContextId != query.ContextId) return empty;

        // The anchor's own millisecond, read once and shared by both halves of an `around` page -
        // FetchRelativeAsync needs it in full for either direction (see its remarks), and for
        // `around` it is literally the same rows twice.
        var anchorInstant = await ReadInstantAsync(query.ContextId, anchor.CreatedAt);

        List<Message> page;
        if (query.Direction == MessageCursorDirection.Around)
        {
            // Split either side of the anchor.
            var half = Math.Max(1, (query.Limit - 1) / 2);
            var older = await FetchRelativeAsync(query.ContextId, anchor, before: true, limit: half, anchorInstant);
            var newer = await FetchRelativeAsync(query.ContextId, anchor, before: false, limit: half, anchorInstant);

            page = [.. older, anchor, .. newer];
        }
        else
        {
            page = await FetchRelativeAsync(query.ContextId, anchor,
                before: query.Direction == MessageCursorDirection.Before, limit: query.Limit, anchorInstant);
        }

        // Ordinal, matching the byte-wise comparison a Scylla clustering key on a text column
        // performs - and matching GetContextMessagesOlderThanAsync, so every read this repository
        // exposes hands back one order.
        var ordered = page
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        return (ordered, await FetchReactionsAsync(ordered));
    }

    /// <summary>
    /// One side of a cursor page: the <paramref name="limit"/> messages nearest <paramref
    /// name="anchor"/> on the given side of it, in the total order <c>(created_at, message_id)</c>.
    /// </summary>
    /// <param name="anchorInstant">
    /// The anchor's whole millisecond, already read - passed in so an <c>around</c> page reads it
    /// once instead of once per side.
    /// </param>
    private async Task<List<Message>> FetchRelativeAsync(
        string contextId, Message anchor, bool before, int limit, List<Message> anchorInstant)
    {
        // LIMIT 0 is a hard error in CQL, not an empty page.
        if (limit <= 0) return [];

        // The anchor's same-millisecond siblings on the requested side.
        var rows = anchorInstant
            .Where(m => before
                ? string.CompareOrdinal(m.Id, anchor.Id) < 0
                : string.CompareOrdinal(m.Id, anchor.Id) > 0)
            .ToList();

        var remaining = limit - rows.Count;
        if (remaining > 0)
        {
            // A single-column relation on one clustering column is an ordinary slice, and its
            // direction is the only thing ORDER BY is being asked for here - the tie-break is
            // handled above and below rather than trusted to the scan.
            var comparison = before ? "<" : ">";
            var order = before ? "DESC" : "ASC";

            var cql =
                $"SELECT {Message.SelectColumns} FROM messages " +
                $"WHERE context_id = ? AND created_at {comparison} ? " +
                $"ORDER BY created_at {order} LIMIT ?";

            // Materialized immediately: Mapper.FetchAsync hands back a single-pass projection over
            // the driver's RowSet, which self-consumes on enumeration (see GetMessagePageAsync).
            var slice = (await context.Mapper.FetchAsync<Message>(
                cql, contextId, anchor.CreatedAt, remaining)).ToList();

            // A short slice exhausted the range, so whatever millisecond it ends on is already
            // whole.
            if (slice.Count >= remaining)
            {
                var boundary = slice[^1].CreatedAt;
                var wholeGroup = await ReadInstantAsync(contextId, boundary);
                slice = slice.Where(m => m.CreatedAt != boundary).Concat(wholeGroup).ToList();
            }

            rows.AddRange(slice);
        }

        // Nearest-to-the-anchor first, so the trim drops the far end of the range rather than
        // punching a hole in the middle of it. The caller flips the page back to chronological.
        var nearestFirst = before
            ? rows.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id, StringComparer.Ordinal)
            : rows.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal);

        return nearestFirst.Take(limit).ToList();
    }

    /// <summary>Every row of one partition sharing one <c>created_at</c>.</summary>
    private async Task<List<Message>> ReadInstantAsync(string contextId, DateTimeOffset instant) =>
        (await context.Mapper.FetchAsync<Message>(
            $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? AND created_at = ? LIMIT ?",
            contextId, instant, MaxSameInstantGroup)).ToList();

    private async Task<Dictionary<string, List<Reaction>>> FetchReactionsAsync(List<Message> messages)
    {
        const string reactionCql = "SELECT * FROM reactions WHERE context_id = ? AND message_id = ?";
        var reactionResults = await Task.WhenAll(messages.Select(m =>
            context.Mapper.FetchAsync<Reaction>(reactionCql, m.ContextId, m.Id)));

        return messages
            .Zip(reactionResults, (m, reactions) => (m.Id, Reactions: reactions.ToList()))
            .ToDictionary(x => x.Id, x => x.Reactions);
    }

    /// <summary>
    /// Widest a single same-millisecond group may be before <see cref="ReadInstantAsync"/> stops
    /// trying to read all of it.
    /// </summary>
    private const int MaxSameInstantGroup = 10_000;

    /// <summary>
    /// The retention/export forward scan: one page of a partition older than <paramref
    /// name="olderThan"/>, resuming after <paramref name="afterCreatedAt"/>, returned oldest-first.
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetContextMessagesOlderThanAsync(
        string contextId, DateTimeOffset olderThan, DateTimeOffset afterCreatedAt, string afterMessageId, int limit)
    {
        // LIMIT 0 is a hard error in CQL, not an empty page.
        if (limit <= 0) return [];

        // Two single-column relations on one clustering column is an ordinary slice.
        var cql =
            $"SELECT {Message.SelectColumns} FROM messages " +
            "WHERE context_id = ? AND created_at > ? AND created_at < ? " +
            "ORDER BY created_at ASC LIMIT ?";

        // Materialized immediately - Mapper.FetchAsync hands back a single-pass, self-consuming
        // projection over the driver's RowSet (see GetMessagePageAsync).
        var rows = (await context.Mapper.FetchAsync<Message>(
            cql, contextId, afterCreatedAt, olderThan, limit)).ToList();

        // A short page exhausted the slice, so whatever group it ends on is already whole.
        if (rows.Count >= limit)
        {
            var boundary = rows[^1].CreatedAt;

            // Everything already read at that instant is replaced rather than appended to - the two
            // reads overlap by construction.
            var wholeGroup = await ReadInstantAsync(contextId, boundary);

            rows = rows.Where(m => m.CreatedAt != boundary).Concat(wholeGroup).ToList();
        }

        // The wire order is (created_at ASC, message_id DESC); the contract - and the relational
        // backend - is ascending on both.
        return rows
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
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
        // Issued one at a time rather than as a CQL batch.
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