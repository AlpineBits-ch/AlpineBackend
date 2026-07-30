using Echo.Realtime;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Events.Reactions;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Handler.Reaction;

public class ReactionDeletedHandler
{
    public static async Task Handle(ReactionRemoved reactionDeleted, IHubContext<EchoRealtimeHub> hubContext, IMessageBus bus, MicroserviceContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(reactionDeleted.ConversationId))
        {
            var conversationMembers = await ctx.Members
                .Where(m => m.ConversationId == reactionDeleted.ConversationId && m.UserId != reactionDeleted.UserId)
                .AsNoTracking().ToListAsync();

            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.ReactionRemoved", reactionDeleted);
        }

        if (!string.IsNullOrWhiteSpace(reactionDeleted.ChannelId))
        {
            await bus.SendAsync(new Guild.Contracts.Bus.Events.ReactionRemovedEvent
            {
                ChannelId = reactionDeleted.ChannelId,
                MessageId = reactionDeleted.MessageId,
                Emoji = reactionDeleted.Emoji,
                UserId = reactionDeleted.UserId,
            });
        }
    }
}