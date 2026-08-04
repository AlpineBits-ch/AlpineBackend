using Echo.Realtime;
using Messaging.Application.Services.Privacy;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Realtime;

/// <summary>
/// T2-18, typing half.
///
/// <para><b>Reciprocal, and enforced here rather than in the client.</b> A user who does not send
/// typing indicators does not receive them either - anything else lets somebody take without
/// giving, which is the property that makes the setting useless the moment one person notices. So
/// the emit is dropped on two independent grounds: the typist has the setting off, or the
/// particular peer about to be told has it off.</para>
///
/// <para>This is the emit site: nothing leaves the server. Filtering at the render site would leave
/// the fact on the wire, where any client that ignores the flag - or any modified one - can read
/// it.</para>
/// </summary>
public class StartConversationTypingHandler
{
    public static async Task Handle(StartConversationTypingCommand cmd, MicroserviceContext context,
        PrivacySettingsCache privacySettings, IHubContext<EchoRealtimeHub> hub)
    {
        var conversation = await context.Conversations.Include(c => c.Members).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConversationId);

        if (conversation is null) return;

        var everyone = conversation.Members
            .Select(m => m.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // One batched read covering the typist and every peer.
        var settings = await privacySettings.GetAsync(everyone);
        var typistEmits = !settings.TryGetValue(cmd.UserId, out var typist) || typist.SendTypingIndicators;

        foreach (var member in everyone)
        {
            // The typist's own devices keep getting the echo they always got - it is their own
            // typing state, and suppressing it would be a behaviour change with no privacy in it.
            if (!string.Equals(member, cmd.UserId, StringComparison.Ordinal))
            {
                if (!typistEmits) continue;

                // The reciprocity half. A peer who withholds their own typing state does not get to
                // watch anyone else's.
                if (settings.TryGetValue(member, out var peer) && !peer.SendTypingIndicators) continue;
            }

            await hub.Clients.User(member).SendAsync("conversation.UserTyping",
                new { conversationId = cmd.ConversationId, userId = cmd.UserId });
        }
    }
}
