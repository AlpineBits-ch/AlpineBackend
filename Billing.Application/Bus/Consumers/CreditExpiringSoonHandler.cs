using Billing.Contracts.Bus.Events;
using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Billing.Application.Bus.Consumers;

/// <summary>Turns the expiry warning into the push a client renders.</summary>
public class CreditExpiringSoonHandler
{
    public static Task Handle(
        CreditExpiringSoon message,
        IHubContext<EchoRealtimeHub> hub,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return hub.Clients.User(message.UserId).SendAsync(
            CreditRealtimeEvents.ExpiringSoon, message, cancellationToken);
    }
}
