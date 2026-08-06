using Echo.Realtime;

using Guild.Application.Services;
using Guild.Domain.Events.Wiki;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Application.Bus.Events.Wiki;

public class WikiCommentCreatedHandler
{
    public async Task Handle(WikiCommentCreated @event, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service)
    {
        var presence = await service.GetGuildPresenceAsync(@event.GuildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.WikiCommentCreated", new
            {
                CommentId = @event.CommentId, PageId = @event.PageId, GuildId = @event.GuildId, AuthorId = @event.AuthorId,
            });
    }
}
