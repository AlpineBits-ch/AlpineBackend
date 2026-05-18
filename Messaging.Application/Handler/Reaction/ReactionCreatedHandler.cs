using Messaging.Application.Hubs;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Events.Reactions;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Handler.Reaction;

public class ReactionCreatedHandler
{
    public static async Task Handle(ReactionCreated reactionCreated, IHubContext<MessagingHub> hubContext, IMessageBus bus, MicroserviceContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(reactionCreated.ConversationId))
        {
            var conversationMembers = await ctx.Members
                .Where(m => m.ConversationId == reactionCreated.ConversationId && m.UserId != reactionCreated.UserId)
                .AsNoTracking().ToListAsync();
            
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("ReactionCreated", reactionCreated);
        }
    }
}