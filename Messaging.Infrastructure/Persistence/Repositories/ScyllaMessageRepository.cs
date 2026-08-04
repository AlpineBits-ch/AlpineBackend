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
    ///
    /// <para>Offset paging, and deprecated for it - it drifts as messages arrive underneath the
    /// reader, which no amount of care here can fix (see <see cref="MessagePageQuery"/>). What it can
    /// avoid is the <i>other</i> way this used to lose rows: <c>LIMIT skip + take</c> cuts the scan
    /// wherever it lands, and inside a same-millisecond group the scan's tie-break runs
    /// message_id ASC while newest-first wants it DESC - so the cut kept the wrong members of the
    /// boundary group, and the same message could be absent from page 1 and from page 2 both. The
    /// boundary group is now completed before the offset is applied, exactly as in
    /// <see cref="FetchRelativeAsync"/> and <see cref="GetContextMessagesOlderThanAsync"/>.</para>
    /// </summary>
    private async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageAsync(
        string contextId, int take, int skip)
    {
        if (take <= 0) return (new List<Message>(), new Dictionary<string, List<Reaction>>());
        if (skip < 0) skip = 0;

        var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";
        var wanted = skip + take;

        // ToList() here is load-bearing, not a style choice: Mapper.FetchAsync returns a lazy
        // projection over the driver's RowSet, whose enumerator *dequeues* from an internal
        // ConcurrentQueue<Row>. It is a single-pass, self-consuming sequence - enumerating it a
        // second time yields nothing. Materializing once up front is the only safe way to read it
        // more than once.
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
        // has to be resolved to its actual sort position first. This rides the secondary index on
        // message_id (see ScyllaContext.RunMigrationsAsync).
        var anchor = await GetMessageAsync(query.AnchorMessageId);
        if (anchor is null || anchor.ContextId != query.ContextId) return empty;

        // The anchor's own millisecond, read once and shared by both halves of an `around` page -
        // FetchRelativeAsync needs it in full for either direction (see its remarks), and for
        // `around` it is literally the same rows twice.
        var anchorInstant = await ReadInstantAsync(query.ContextId, anchor.CreatedAt);

        List<Message> page;
        if (query.Direction == MessageCursorDirection.Around)
        {
            // Split either side of the anchor. The anchor itself is returned too, so the two
            // halves are sized to leave room for it rather than each taking the full limit.
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
    /// One side of a cursor page: the <paramref name="limit"/> messages nearest <paramref name="anchor"/>
    /// on the given side of it, in the total order <c>(created_at, message_id)</c>.
    ///
    /// <para><b>This deliberately does not use a row-tuple cursor, for the same reason
    /// <see cref="GetContextMessagesOlderThanAsync"/> does not.</b> <c>messages</c> is clustered
    /// <c>(created_at DESC, message_id ASC)</c> - mixed directions - while a CQL multi-column
    /// relation <c>(created_at, message_id) &lt; (?, ?)</c> is plain ascending-lexicographic on both
    /// components. <b>Neither order this table can be read in matches that.</b> Read naturally
    /// (<c>ORDER BY created_at DESC</c>) the tie-break ascends while the timestamp descends; read
    /// <c>ORDER BY created_at ASC</c> the cluster reverses <i>both</i> columns, so the tie-break
    /// descends while the timestamp ascends. Either way, inside one millisecond the scan hands back
    /// the members <i>furthest</i> from the anchor first and <c>LIMIT</c> keeps exactly those - so a
    /// "two messages immediately before this one" page returned the two furthest, and the next page,
    /// anchored on what the client was given, was already past the ones it had skipped. The cluster
    /// accepts the statement without complaint; the only symptom is a message quietly missing from a
    /// user's scrollback, and only when a page boundary happens to land inside a same-millisecond
    /// group - which bursts, bulk sends, bot output and imported history all produce. Verified
    /// against a live node; see <c>ScyllaCursorPagingTests</c>, which failed on exactly this before
    /// this method was rewritten.</para>
    ///
    /// <para><b>So the tie-break is resolved here rather than by the cluster.</b> The anchor's own
    /// millisecond is read whole and split by id, which is the only part of the range where the
    /// tie-break matters relative to the anchor; the rest is an ordinary single-column slice on
    /// <c>created_at</c>, whose own boundary millisecond is completed the same way before anything is
    /// trimmed. Nothing is ever cut inside a group that has not been read in full first.</para>
    ///
    /// <para><b>Page size is unchanged: a page still holds at most <paramref name="limit"/> rows.</b>
    /// This is the one place where this differs from <see cref="GetContextMessagesOlderThanAsync"/>,
    /// which over-returns to keep whole millisecond groups together, and it can differ because the
    /// resume cursor here is a message <i>id</i>. A group split across two pages is picked up exactly
    /// where it was left, because the next call resolves that id and re-reads its millisecond. The
    /// retention scan's cursor is a bare timestamp and has no such handle, which is why it has to
    /// keep groups whole instead.</para>
    /// </summary>
    /// <param name="anchorInstant">The anchor's whole millisecond, already read - passed in so an
    /// <c>around</c> page reads it once instead of once per side.</param>
    private async Task<List<Message>> FetchRelativeAsync(
        string contextId, Message anchor, bool before, int limit, List<Message> anchorInstant)
    {
        // LIMIT 0 is a hard error in CQL, not an empty page.
        if (limit <= 0) return [];

        // The anchor's same-millisecond siblings on the requested side. Ordinal because that is what
        // the clustering key comparison is; the culture-sensitive default would order ids by rules
        // the cluster has never heard of.
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

            // A short slice exhausted the range, so whatever millisecond it ends on is already whole.
            // A full one may have been cut inside a group, and cut in the wrong place - so that
            // group is re-read in full and replaces what was read of it, before the sort below picks
            // which of its members are actually the nearest.
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

    /// <summary>Every row of one partition sharing one <c>created_at</c>. Equality on the first
    /// clustering column, so this reads the group in the table's own order and needs no
    /// <c>ORDER BY</c> - which is the point, since an ORDER BY is what the tie-break cannot survive
    /// on this table.</summary>
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
    /// trying to read all of it. A conversation would need this many messages stored in one
    /// millisecond to reach it, which is orders of magnitude past anything the write path can
    /// produce; the cap exists so a corrupt or adversarial partition cannot turn one page read into
    /// an unbounded one. Past it, both the retention scan and history paging degrade back to being
    /// able to lose rows inside that one group - the same failure this whole shape exists to
    /// prevent, traded for a bounded read.
    /// </summary>
    private const int MaxSameInstantGroup = 10_000;

    /// <summary>
    /// The retention/export forward scan: one page of a partition older than <paramref name="olderThan"/>,
    /// resuming after <paramref name="afterCreatedAt"/>, returned oldest-first.
    ///
    /// <para><b>This deliberately does not use a row-tuple cursor, and the reason is the whole point
    /// of this method's shape.</b> The messages table is clustered
    /// <c>(created_at DESC, message_id ASC)</c> - mixed directions. CQL's multi-column relation
    /// <c>(created_at, message_id) &gt; (?, ?)</c> is plain ascending-lexicographic on both
    /// components, but <c>ORDER BY created_at ASC</c> on a mixed-order table reverses <b>both</b>
    /// clustering columns, so the rows arrive as <c>(created_at ASC, message_id DESC)</c>. The
    /// comparison and the scan therefore disagree on the tie-break column: resuming after the last
    /// row of a page skips every same-millisecond sibling below it, permanently and silently.
    /// Verified against a live node - see <c>ScyllaDmRetentionRangeDeleteTests</c>, which failed on
    /// exactly this before the query was rewritten. On a delete path that means a user's retention
    /// setting quietly under-deletes forever; on the export path it means an Art. 15 archive quietly
    /// omits messages. There is no ordering the table can produce that a row-tuple cursor is correct
    /// against, because the two clustering columns point opposite ways.</para>
    ///
    /// <para><b>So the cursor is the timestamp alone, and a page never contains half of one.</b>
    /// Whole same-millisecond groups are consumed or none of it is, which is what makes a bare
    /// <c>created_at &gt; ?</c> resume exact. When the page cap lands inside a group, the rest of
    /// that group is read back in a second, equality-bounded query and merged - so a page can come
    /// back slightly larger than <paramref name="limit"/>, and "fewer rows than the limit" keeps
    /// meaning "end of the range", which is the condition every caller loops on.</para>
    ///
    /// <para><paramref name="afterMessageId"/> is part of the interface for the relational backend,
    /// whose ORDER BY can name both columns ascending and whose cursor therefore is a genuine
    /// two-part one. It is unused here. Both backends return the same rows in the same
    /// <c>(created_at, message_id)</c> ascending order, which is what the contract actually
    /// promises; only the resume mechanics differ.</para>
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetContextMessagesOlderThanAsync(
        string contextId, DateTimeOffset olderThan, DateTimeOffset afterCreatedAt, string afterMessageId, int limit)
    {
        // LIMIT 0 is a hard error in CQL, not an empty page.
        if (limit <= 0) return [];

        // Two single-column relations on one clustering column is an ordinary slice. (The upper
        // bound used to be written as a one-component tuple to sit alongside a multi-column lower
        // bound, because Cassandra refuses to mix the two forms on the same column. With the lower
        // bound single-column too, the restriction no longer applies to anything here.)
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