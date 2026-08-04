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
        ChannelAudienceService audience,
        PrivacySettingsCache privacySettings,
        BlockCache blocks)
    {
        // Privacy spec T2-18, enforced at the emit site rather than the render site: a user who has
        // turned typing indicators off does not produce one, so it never reaches the wire at all.
        // Filtering on the receiving client would leave the fact on every recipient's socket, which
        // is precisely what the setting is about.
        var typerSettings = await privacySettings.GetAsync(message.UserId);
        if (!typerSettings.SendTypingIndicators) return;

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

        if (viewerIds.Count == 0) return;

        // No realtime event flows between a blocked pair (T0-3).
        var blockView = await blocks.GetAsync([message.UserId]);
        var candidates = blockView.Reachable(message.UserId, viewerIds);

        if (candidates.Count == 0) return;

        // Reciprocal (T2-18): someone who does not send typing indicators does not receive them.
        // Without that, the setting is a way to take without giving, which is the property that
        // makes it unusable in practice - everyone turns it off and nobody sees anything.
        // The typer is exempt from the filter because they already passed it above.
        var recipientSettings = await privacySettings.GetAsync(candidates);

        var recipients = candidates
            .Where(id => string.Equals(id, message.UserId, StringComparison.Ordinal)
                         || (recipientSettings.TryGetValue(id, out var settings) && settings.SendTypingIndicators))
            .ToList();

        if (recipients.Count == 0) return;

        await hub.Clients.Users(recipients).SendAsync("guild.UserTyping",
            new { channelId = message.ChannelId, userId = message.UserId });
    }
}
