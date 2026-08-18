using Echo.Realtime;
using Messaging.Application.Services;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Conversation;

public class ConversationMemberLeftHandler
{
    public static async Task Handle(ConversationMemberRemoved memberRemoved,
        MicroserviceContext ctx, IDistributedCache cache, IHubContext<EchoRealtimeHub> hubContext,
        ConversationPermissionService conversationPermissions)
    {
        // Before the broadcast: the cached membership set is trusted outright on the read side, so
        // until it is dropped the person who just left still passes the gate on message history,
        // search, pins and attachments.
        await conversationPermissions.InvalidateAsync(memberRemoved.UserId);

        var conversationMembers = await ctx.Members.Where(m => m.ConversationId == memberRemoved.ConversationId && m.UserId != memberRemoved.UserId).AsNoTracking().ToListAsync();
        await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MemberLeft", memberRemoved);
    }
}