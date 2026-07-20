using Echo.Realtime;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Realtime;

public class StartConversationTypingHandler
{
    public static async Task Handle(StartConversationTypingCommand cmd, MicroserviceContext context, IHubContext<EchoRealtimeHub> hub)
    {
        var conversation = await context.Conversations.Include(c => c.Members).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConversationId);

        if (conversation is null) return;

        foreach (var member in conversation.Members)
        {
            await hub.Clients.User(member.UserId).SendAsync("conversation.UserTyping",
                new { conversationId = cmd.ConversationId, userId = cmd.UserId });
        }
    }
}
