using Echo.Realtime;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Realtime;

public class UpdateConversationReadHandler
{
    public static async Task Handle(UpdateConversationReadCommand cmd, MicroserviceContext context)
    {
        var member = await context.Members
            .Where(c => c.UserId == cmd.UserId && c.ConversationId == cmd.ConversationId)
            .FirstOrDefaultAsync();
        member?.LastReadMessageId = cmd.Id;
        await context.SaveChangesAsync();
    }
}
