using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Messages;

/// <summary>
/// Mirrors MessageCreatedHandler's channel->guild resolution, but for deletes - Bots.Application
/// needs GuildId to know which installed bots to dispatch MESSAGE_DELETE to, and the raw
/// MessageDeletedForChannel event (published by Messaging) doesn't carry it.
/// </summary>
public class MessageDeletedForChannelHandler
{
    private string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(MessageDeletedForChannel message, MicroserviceContext context, IDistributedCache cache,
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrateService, ChannelAudienceService audience,
        IMessageBus bus, ILogger<MessageDeletedForChannelHandler> logger)
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

        // Channel-scoped audience, the same one the bulk delete uses. Messaging only announces a
        // delete to a conversation, so without this a guild channel's readers keep the message.
        var presence = await hydrateService.GetGuildPresenceAsync(guildId);
        var viewerIds = await audience.FilterToViewersAsync(message.ChannelId, presence.Select(p => p.UserId));

        await hub.Clients.Users(viewerIds).SendAsync("guild.MessageDeleted", new
        {
            GuildId = guildId,
            message.ChannelId,
            message.MessageId,
            message.AuthorId,
        });

        await bus.PublishAsync(new MessageDeletedForBots
        {
            GuildId = guildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
        });

        // Keeps the forum post card's reply count and the inbox unread badge honest.
        var channel = await context.Channels.FirstOrDefaultAsync(c => c.Id == message.ChannelId);

        if (channel is not null && channel.MessageCount > 0) channel.MessageCount--;

        // The broadcast ping goes with the message that carried it, so a deleted @everyone stops
        // showing up in anyone's Mentions tab.
        var broadcasts = await context.ChannelBroadcastMentions
            .Where(b => b.MessageId == message.MessageId)
            .ToListAsync();

        if (broadcasts.Count > 0) context.ChannelBroadcastMentions.RemoveRange(broadcasts);
    }
}
