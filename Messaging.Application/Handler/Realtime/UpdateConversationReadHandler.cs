using Echo.Realtime;
using Messaging.Application.Services.Privacy;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Realtime;

/// <summary>
/// Records where a member has read up to, and - T2-18 - decides who is allowed to hear about it.
///
/// <para>The read position is <b>always</b> stored: it is what drives the reader's own unread badge,
/// and a privacy setting about telling other people must not cost the user their own state.
/// <c>SendReadReceipts</c> governs the emit, not the write.</para>
///
/// <para><b>Reciprocal.</b> A user who does not send read receipts does not receive them. Both
/// halves are checked before anything leaves: the reader's setting, and then each peer's, so a
/// person who has turned receipts off can neither be seen nor see.</para>
///
/// <para>The peer-visible half of a read receipt has two surfaces and both are covered:
/// <c>conversation.ReadReceipt</c> emitted here, and <c>ConversationMemberDto.LastReadMessageId</c>
/// on the conversation projections, which <c>ConversationController</c> scrubs for the same
/// reason.</para>
/// </summary>
public class UpdateConversationReadHandler
{
    /// <summary>The realtime event carrying one member's read position to the others. New, and
    /// additive: no client is required to consume it, and the stored value it mirrors is still
    /// projected onto the conversation for anyone entitled to see it.</summary>
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
