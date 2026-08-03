using Echo.Realtime;
using Guild.Application.Services;
using Guild.Domain.Enums;
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
        IDistributedCache cache,
        GuildPermissionService permissionService,
        ChannelAudienceService audience)
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

        // The channel id arrives from the client over the hub, and this was the only hub-forwarded
        // guild command with no authorization of any kind: any authenticated user could name a
        // channel in a guild they had never joined and have their own user id rendered as "typing"
        // inside it. Someone shown as typing must at least be able to post there.
        if (!await permissionService.CanUserPerformActionAsync(message.UserId, message.ChannelId, Permissions.SendMessages))
            return;

        // Channel-scoped audience - see ChannelAudienceService.
        var presence = await service.GetGuildPresenceAsync(cachedGuildId);
        var viewerIds = await audience.FilterToViewersAsync(message.ChannelId, presence.Select(p => p.UserId));

        await hub.Clients.Users(viewerIds).SendAsync("guild.UserTyping",
            new { channelId = message.ChannelId, userId = message.UserId });
    }
}
