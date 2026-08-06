using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiCommentUpdatedHandler
{
    public async Task Handle(WikiCommentUpdated @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiCommentUpdated", new
            {
                CommentId = @event.CommentId, PageId = @event.PageId, GuildId = @event.GuildId,
            });
    }
}
