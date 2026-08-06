using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiPageReactionRemovedHandler
{
    public async Task Handle(WikiPageReactionRemoved @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiPageReactionRemoved", new
            {
                PageId = @event.PageId, GuildId = @event.GuildId, UserId = @event.UserId, Emoji = @event.Emoji,
            });
    }
}
