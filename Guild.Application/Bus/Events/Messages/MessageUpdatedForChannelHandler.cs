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
/// Mirrors MessageCreatedHandler's channel->guild resolution - broadcasts guild.MessageUpdated to
/// guild members over the hub and republishes MessageUpdatedForBots (GuildId attached) for
/// Bots.Application, which can't join the SignalR/Redis backplane the hub broadcast rides on.
/// </summary>
public class MessageUpdatedForChannelHandler
{
    private string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(MessageUpdatedForChannel message, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, IMessageBus bus, ILogger<MessageUpdatedForChannelHandler> logger,
        ChannelAudienceService audience)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var guildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(guildId))
        {
            guildId = await context.Channels.Where(c => c.Id == message.ChannelId).Select(c => c.GuildId).FirstOrDefaultAsync();
            if (guildId is null)
            {
                logger.LogWarning($"Channel with ID {message.ChannelId} not found in context");
                return;
            }
            await cache.SetStringAsync(channelKey, guildId);
        }

        // Channel-scoped audience - this payload carries the message content. See
        // ChannelAudienceService.
        var presence = await service.GetGuildPresenceAsync(guildId);
        var viewerIds = await audience.FilterToViewersAsync(
            message.ChannelId, presence.Select(p => p.UserId).Except([message.AuthorId]));

        await hub.Clients.Users(viewerIds).SendAsync("guild.MessageUpdated", message);

        await bus.PublishAsync(new MessageUpdatedForBots
        {
            GuildId = guildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
            Content = message.Content,
            AuthorId = message.AuthorId,
            EmbedsJson = message.EmbedsJson,
        });
    }
}
