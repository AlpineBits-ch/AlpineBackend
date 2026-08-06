using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiPageReactionAddedHandler
{
    public async Task Handle(WikiPageReactionAdded @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiPageReactionAdded", new
            {
                PageId = @event.PageId, GuildId = @event.GuildId, UserId = @event.UserId, Emoji = @event.Emoji,
            });
    }
}
