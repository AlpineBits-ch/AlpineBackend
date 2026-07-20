using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiCategoryUpdatedHandler
{
    public async Task Handle(WikiCategoryUpdated @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiCategoryUpdated", new { CategoryId = @event.CategoryId, GuildId = @event.GuildId, ParentCategoryId = @event.ParentCategoryId });
    }
}
