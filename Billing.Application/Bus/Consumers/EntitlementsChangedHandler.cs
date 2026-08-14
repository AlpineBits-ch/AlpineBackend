using Billing.Contracts.Bus.Events;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Echo.Realtime;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Billing.Application.Bus.Consumers;

/// <summary>
/// Turns <c>billing.EntitlementsChanged</c> into the <c>entitlements.Changed</c> push that clients
/// listen for.
/// </summary>
public class EntitlementsChangedHandler
{
    /// <summary>Guild's own handler clamps here, so asking for more achieves nothing.</summary>
    private const int FanOutLimit = 1000;

    public static async Task Handle(
        EntitlementsChanged message,
        IHubContext<EchoRealtimeHub> hub,
        IMessageBus bus,
        ILogger<EntitlementsChangedHandler> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var subject = new EntitlementSubject(message.SubjectKind, message.SubjectId);
        var payload = EntitlementsChangedDto.For(subject, message.Version, message.ChangedKeys);

        if (message.SubjectKind == SubjectKind.User)
        {
            await hub.Clients.User(message.SubjectId).SendAsync(
                EntitlementRealtimeEvents.Changed, payload, cancellationToken);
            return;
        }

        // Membership belongs to Guild and Billing holds none of it, so the recipient list is asked
        // for rather than guessed.
        List<string> recipients;
        try
        {
            var members = await bus.InvokeAsync<ListGuildMembersResponse>(
                new ListGuildMembersRequest { GuildId = message.SubjectId, Limit = FanOutLimit });

            recipients = members.Members.Where(member => !member.IsBot).Select(member => member.UserId).ToList();
        }
        catch (Exception exception)
        {
            // Not rethrown.
            logger.LogWarning(exception,
                "Could not list members of guild {GuildId} to push entitlements.Changed. Clients will "
                + "pick the change up when their cache expires.", message.SubjectId);
            return;
        }

        if (recipients.Count == 0) return;

        await hub.Clients.Users(recipients).SendAsync(
            EntitlementRealtimeEvents.Changed, payload, cancellationToken);
    }
}
