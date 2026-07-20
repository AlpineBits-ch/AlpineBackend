using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Handler.Realtime;

public class UserConnectedHandler
{
    public static async Task Handle(UserConnected cmd, IMessageBus bus, IHubContext<EchoRealtimeHub> hub)
    {
        await bus.PublishAsync(new UserActiveEvent() { UserId = cmd.UserId });

        var relationships = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = cmd.UserId });

        foreach (var relationship in relationships.Profile?.Relationships ?? [])
        {
            await hub.Clients.User(relationship.UserId).SendAsync("presence.UserOnline", cmd.UserId);
        }
    }
}
