using Echo.Realtime;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Bus.Events.Realtime;

public class GuildTypingHandler
{
    public async Task Handle(
        StartGuildTypingCommand message,
        IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService service,
        MicroserviceContext microserviceContext,
        IDistributedCache cache)
    {
        var cacheId = $"channel_map:{message.ChannelId}";

        var cachedGuildId = await cache.GetStringAsync(cacheId);
        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
            var channel = await microserviceContext.Channels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == message.ChannelId);

            cachedGuildId = channel?.GuildId;
            if (string.IsNullOrWhiteSpace(cachedGuildId)) return;
            await cache.SetStringAsync(cacheId, cachedGuildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.UserTyping",
            new { channelId = message.ChannelId, userId = message.UserId });
    }
}
