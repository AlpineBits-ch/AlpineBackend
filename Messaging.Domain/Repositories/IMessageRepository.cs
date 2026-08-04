using Messaging.Domain.Entities;

namespace Messaging.Domain.Repositories;

public interface IMessageRepository
{
    public  Task<Message> CreateMessageAsync(Message message);
    public Task<Message?> GetMessageAsync(string messageId);

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByConversationIdAsync(
        string conversationId, int take, int skip);

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByContextIdAsync(
        string contextId, int take, int skip);

    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByChannelIdAsync(
        string channelId, int take, int skip);

    /// <summary>Cursor-anchored page, oldest-first like the offset overloads above. Returns an
    /// empty page when the anchor id does not exist in the context - the caller cannot
    /// distinguish that from "no messages there", which is deliberate: both mean the client's
    /// cursor is stale and it should re-fetch from the top.</summary>
    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageByCursorAsync(
        MessagePageQuery query);

    /// <summary>
    /// One page of a context's messages older than <paramref name="olderThan"/>, oldest first,
    /// starting strictly after the supplied cursor. Backs the user-set DM retention sweep (T2-22).
    ///
    /// <para><b>Every author, not just one.</b> The sweep only ever deletes the messages one
    /// particular user sent, but filtering by author in the query is not available on the Scylla
    /// side - <c>author_id</c> is not part of the messages primary key, so restricting on it would
    /// need <c>ALLOW FILTERING</c> and a partition scan. Returning the page and letting the caller
    /// pick its own rows out of it is what makes the sweep affordable; the cursor is what stops it
    /// stalling forever behind a run of messages belonging to the other side of the conversation,
    /// which a plain "oldest N" read would do the moment those N were not the sweeper's.</para>
    ///
    /// <para>A non-positive <paramref name="limit"/> returns nothing rather than being passed
    /// through: Scylla treats <c>LIMIT 0</c> as an error, not as an empty result.</para>
    /// </summary>
    public Task<IReadOnlyList<Message>> GetContextMessagesOlderThanAsync(
        string contextId,
        DateTimeOffset olderThan,
        DateTimeOffset afterCreatedAt,
        string afterMessageId,
        int limit);

    public Task<Message> UpdateMessageAsync(Message message);

    public Task DeleteMessageAsync(Message message);

    /// <summary>Deletes a batch of already-loaded messages. Takes entities rather than ids because
    /// the Scylla backing store needs the full primary key (context_id, created_at, message_id) to
    /// delete a row, and only created_at makes that resolvable - an id-only overload would have to
    /// re-read every message anyway.</summary>
    public Task DeleteMessagesAsync(IReadOnlyCollection<Message> messages);

    public Task<Message> PinMessageAsync(Message message, string pinnedById);

    public Task<Message> UnpinMessageAsync(Message message);

    public Task<ICollection<Message>> GetPinnedMessagesAsync(string contextId, int limit = 50);

    public Task AddReactionAsync(Reaction reaction);

    public Task RemoveReactionAsync(string contextId, string messageId, string emoji, string userId);
}