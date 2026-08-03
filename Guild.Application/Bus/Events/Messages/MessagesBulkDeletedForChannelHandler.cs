using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Messages;

/// <summary>
/// The aggregate half of a bulk delete. Each removed message also arrives here individually as a
/// <see cref="MessageDeletedForChannel"/> (handled next door), which is what actually maintains
/// the search index, forum reply counts and per-message bot dispatches - this handler adds only
/// the two things that are genuinely batch-shaped: one realtime push so a client can drop the
/// whole range in a single update, and Discord's MESSAGE_DELETE_BULK for bots.
/// </summary>
public class MessagesBulkDeletedForChannelHandler
{
    private static string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(MessagesBulkDeletedForChannel message, MicroserviceContext context,
        IDistributedCache cache, IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrateService,
        IMessageBus bus, ILogger<MessagesBulkDeletedForChannelHandler> logger,
        ChannelAudienceService audience)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var guildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(guildId))
        {
            guildId = await context.Channels
                .Where(c => c.Id == message.ChannelId)
                .Select(c => c.GuildId)
                .FirstOrDefaultAsync();

            if (guildId is null)
            {
                logger.LogWarning("Channel with ID {ChannelId} not found in context", message.ChannelId);
                return;
            }

            await cache.SetStringAsync(channelKey, guildId);
        }

        // Channel-scoped audience - see ChannelAudienceService.
        var presence = await hydrateService.GetGuildPresenceAsync(guildId);
        var viewerIds = await audience.FilterToViewersAsync(message.ChannelId, presence.Select(p => p.UserId));

        await hub.Clients.Users(viewerIds).SendAsync("guild.MessagesBulkDeleted", new
        {
            GuildId = guildId,
            message.ChannelId,
            message.MessageIds,
            message.ActorUserId,
        });

        await bus.PublishAsync(new MessagesBulkDeletedForBots
        {
            GuildId = guildId,
            ChannelId = message.ChannelId,
            MessageIds = message.MessageIds,
        });
    }
}
