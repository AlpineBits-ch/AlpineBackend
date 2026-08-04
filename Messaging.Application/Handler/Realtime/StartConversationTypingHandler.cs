using Echo.Realtime;
using Messaging.Application.Services.Privacy;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Realtime;

/// <summary>T2-18, typing half.</summary>
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

                // The reciprocity half.
                if (settings.TryGetValue(member, out var peer) && !peer.SendTypingIndicators) continue;
            }

            await hub.Clients.User(member).SendAsync("conversation.UserTyping",
                new { conversationId = cmd.ConversationId, userId = cmd.UserId });
        }
    }
}
