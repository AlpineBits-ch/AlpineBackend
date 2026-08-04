using Echo.Realtime;
using Messaging.Application.Services.Privacy;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Realtime;

/// <summary>
/// Records where a member has read up to, and - T2-18 - decides who is allowed to hear about it.
/// </summary>
public class UpdateConversationReadHandler
{
    /// <summary>The realtime event carrying one member's read position to the others.</summary>
    public const string ReadReceiptEvent = "conversation.ReadReceipt";

    public static async Task Handle(UpdateConversationReadCommand cmd, MicroserviceContext context,
        PrivacySettingsCache privacySettings, IHubContext<EchoRealtimeHub> hub)
    {
        var member = await context.Members
            .Where(c => c.UserId == cmd.UserId && c.ConversationId == cmd.ConversationId)
            .FirstOrDefaultAsync();

        if (member is null) return;

        member.LastReadMessageId = cmd.Id;
        await context.SaveChangesAsync();

        var peers = await context.Members
            .Where(m => m.ConversationId == cmd.ConversationId && m.UserId != cmd.UserId)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync();

        if (peers.Count == 0) return;

        var settings = await privacySettings.GetAsync(peers.Append(cmd.UserId));

        if (settings.TryGetValue(cmd.UserId, out var reader) && !reader.SendReadReceipts) return;

        foreach (var peer in peers)
        {
            if (settings.TryGetValue(peer, out var other) && !other.SendReadReceipts) continue;

            await hub.Clients.User(peer).SendAsync(ReadReceiptEvent,
                new { conversationId = cmd.ConversationId, userId = cmd.UserId, messageId = cmd.Id });
        }
    }
}
