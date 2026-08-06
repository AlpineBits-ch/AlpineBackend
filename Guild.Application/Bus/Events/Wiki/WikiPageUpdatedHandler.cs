using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiPageUpdatedHandler
{
    public async Task Handle(WikiPageUpdated @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service, MicroserviceContext ctx)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiPageUpdated", new { PageId = @event.PageId, GuildId = @event.GuildId, EditorId = @event.EditorId });

        await NotifyWatchersAsync(@event, hub, ctx);
    }

    /// <summary>The watcher half.</summary>
    private static async Task NotifyWatchersAsync(WikiPageUpdated @event, IHubContext<EchoRealtimeHub> hub, MicroserviceContext ctx)
    {
        var watchers = await ctx.WikiPageWatchers
            .Where(w => w.PageId == @event.PageId && w.UserId != @event.EditorId)
            .Select(w => w.UserId)
            .ToListAsync();

        if (watchers.Count == 0) return;

        // The title comes along so the client can render "X was updated" without a fetch per event.
        var title = await ctx.WikiPages
            .Where(p => p.Id == @event.PageId)
            .Select(p => p.Title)
            .FirstOrDefaultAsync();

        await hub.Clients.Users(watchers)
            .SendAsync("guild.WikiPageWatchedUpdate", new
            {
                PageId = @event.PageId,
                GuildId = @event.GuildId,
                Title = title,
                EditorId = @event.EditorId,
            });
    }
}
