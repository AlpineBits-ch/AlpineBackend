using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Events.Reactions;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Handler.Reaction;

public class ReactionCreatedHandler
{
    public static async Task Handle(ReactionCreated reactionCreated, IHubContext<EchoRealtimeHub> hubContext, IMessageBus bus, MicroserviceContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(reactionCreated.ConversationId))
        {
            var conversationMembers = await ctx.Members
                .Where(m => m.ConversationId == reactionCreated.ConversationId && m.UserId != reactionCreated.UserId)
                .AsNoTracking().ToListAsync();

            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.ReactionCreated", reactionCreated);
        }

        // Previously nothing fanned a channel reaction out at all - Guild.Application's
        // ReactionHandler already knows how to broadcast guild.ReactionCreated and republish for
        // Bots, it just never received anything to react to.
        if (!string.IsNullOrWhiteSpace(reactionCreated.ChannelId))
        {
            await bus.SendAsync(new Guild.Contracts.Bus.Events.ReactionCreatedEvent
            {
                ChannelId = reactionCreated.ChannelId,
                MessageId = reactionCreated.MessageId,
                Emoji = reactionCreated.Emoji,
                UserId = reactionCreated.UserId,
                EmojiId = reactionCreated.EmojiId,
            });
        }
    }
}