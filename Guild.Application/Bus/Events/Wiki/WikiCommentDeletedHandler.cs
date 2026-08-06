using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiCommentDeletedHandler
{
    public async Task Handle(WikiCommentDeleted @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiCommentDeleted", new
            {
                CommentId = @event.CommentId, PageId = @event.PageId, GuildId = @event.GuildId,
            });
    }
}
