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
        var cql = "SELECT * FROM messages WHERE conversation_id = ? ORDER BY created_at DESC LIMIT ?";
        
          
        
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
        var cql = "SELECT * FROM messages WHERE conversation_id = ? ORDER BY created_at DESC LIMIT ?";
        
          
        
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
        var cql = "SELECT * FROM messages WHERE channel_id = ? ORDER BY created_at DESC LIMIT ?";
        
          
        
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
}