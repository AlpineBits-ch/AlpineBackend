using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Infrastructure.Persistence.Repositories;

// Every mutating method here is called exclusively from Wolverine-managed code (bus handlers or
// WolverineHttp endpoints - see IMessageRepository's call sites), which auto-wraps the ambient
// MicroserviceContext in a transaction and commits it once the handler/endpoint returns
// (opts.Policies.AutoApplyTransactions() + UseEntityFrameworkCoreTransactions()).
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

        var ordered = page.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToList();
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
