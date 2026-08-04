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

    /// <summary>Cursor-anchored page, oldest-first like the offset overloads above.</summary>
    public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageByCursorAsync(
        MessagePageQuery query);

    /// <summary>
    /// One page of a context's messages older than <paramref name="olderThan"/>, oldest first,
    /// starting strictly after the supplied cursor.
    /// </summary>
    public Task<IReadOnlyList<Message>> GetContextMessagesOlderThanAsync(
        string contextId,
        DateTimeOffset olderThan,
        DateTimeOffset afterCreatedAt,
        string afterMessageId,
        int limit);

    public Task<Message> UpdateMessageAsync(Message message);

    public Task DeleteMessageAsync(Message message);

    /// <summary>Deletes a batch of already-loaded messages.</summary>
    public Task DeleteMessagesAsync(IReadOnlyCollection<Message> messages);

    public Task<Message> PinMessageAsync(Message message, string pinnedById);

    public Task<Message> UnpinMessageAsync(Message message);

    public Task<ICollection<Message>> GetPinnedMessagesAsync(string contextId, int limit = 50);

    public Task AddReactionAsync(Reaction reaction);

    public Task RemoveReactionAsync(string contextId, string messageId, string emoji, string userId);
}