using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Infrastructure.Persistence.Repositories;

public class EfCoreMessageRepository(MicroserviceContext context) : IMessageRepository
{
    public async Task<Message> CreateMessageAsync(Message message)
    {
        context.Messages.Add(message);
        await context.SaveChangesAsync();
        return message;
    }

    public async Task<Message?> GetMessageAsync(string messageId)
    {
        return await context.Messages
            .AsNoTracking()
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId);
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByConversationIdAsync(
        string conversationId, int take, int skip)
    {
        var messages = await context.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Include(m => m.Attachments)
            .ToListAsync();

        messages = messages.OrderBy(m => m.CreatedAt).ToList();
        var reactionsByMessage = await FetchReactionsForMessages(messages);
        return (messages, reactionsByMessage);
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByContextIdAsync(
        string contextId, int take, int skip)
    {
        var messages = await context.Messages
            .AsNoTracking()
            .Where(m => m.ContextId == contextId)
            .OrderByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Include(m => m.Attachments)
            .ToListAsync();

        messages = messages.OrderBy(m => m.CreatedAt).ToList();
        var reactionsByMessage = await FetchReactionsForMessages(messages);
        return (messages, reactionsByMessage);
    }

    public async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByChannelIdAsync(
        string channelId, int take, int skip)
    {
        var messages = await context.Messages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Include(m => m.Attachments)
            .ToListAsync();

        messages = messages.OrderBy(m => m.CreatedAt).ToList();
        var reactionsByMessage = await FetchReactionsForMessages(messages);
        return (messages, reactionsByMessage);
    }

    public async Task<Message> UpdateMessageAsync(Message message)
    {
        context.Messages.Update(message);
        await context.SaveChangesAsync();
        return message;
    }

    public async Task DeleteMessageAsync(Message message)
    {
        context.Messages.Remove(message);
        await context.SaveChangesAsync();
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
