using Guild.Application.Hubs;
using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiPageUpdatedHandler
{
    public async Task Handle(WikiPageUpdated @event, IHubContext<GuildHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("WikiPageUpdated", new { PageId = @event.PageId, GuildId = @event.GuildId });
    }
}
