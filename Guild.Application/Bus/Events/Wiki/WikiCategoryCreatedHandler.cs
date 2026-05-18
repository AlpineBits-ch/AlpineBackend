using Guild.Application.Hubs;
using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiCategoryCreatedHandler
{
    public async Task Handle(WikiCategoryCreated @event, IHubContext<GuildHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("WikiCategoryCreated", new { CategoryId = @event.CategoryId, GuildId = @event.GuildId, ParentCategoryId = @event.ParentCategoryId });
    }
}
