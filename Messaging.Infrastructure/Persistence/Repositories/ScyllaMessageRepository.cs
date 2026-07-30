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

 

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByConversationIdAsync(string conversationId, int take, int skip)
    {
        // messages' PRIMARY KEY is (context_id, created_at, message_id) - context_id is the
        // partition key. conversation_id/channel_id are denormalized metadata columns, not part of
        // the key, so filtering on them requires ALLOW FILTERING (a full partition scan) and Scylla
        // rejects it outright.
        var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";

        var messageItems = await context.Mapper.FetchAsync<Message>(cql, conversationId, skip + take);
        
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


        return (messageItems.ToList(), reactionsByMessage);
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByContextIdAsync(string contextId, int take, int skip)
    {
        // Same partition-key fix as GetMessagesByConversationIdAsync - this was querying by
        // conversation_id (not part of the key, and wrong for channel-scoped context ids too)
        // instead of the actual partition key, context_id.
        var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";

        var messageItems = await context.Mapper.FetchAsync<Message>(cql, contextId, skip + take);
        
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


        return (messageItems.ToList(), reactionsByMessage);
        
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByChannelIdAsync(string channelId, int take, int skip)
    {
        // Same partition-key fix as GetMessagesByConversationIdAsync - channel_id isn't part of
        // messages' PRIMARY KEY (context_id, created_at, message_id), so this required ALLOW
        // FILTERING and Scylla rejected it.
        var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";

        var messageItems = await context.Mapper.FetchAsync<Message>(cql, channelId, skip + take);
        
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


        return (messageItems.ToList(), reactionsByMessage);
        
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