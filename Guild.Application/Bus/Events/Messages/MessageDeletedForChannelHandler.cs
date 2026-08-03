using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
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

        await bus.PublishAsync(new MessageDeletedForBots
        {
            GuildId = guildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
        });

        // Keeps the forum post card's reply count and the inbox unread badge honest. Clamped at zero
        // rather than trusting the counter: it's a denormalized best-effort tally over bus events,
        // so a delete whose create was never seen would otherwise drive it negative. LastActivityAt
        // deliberately isn't rewound - deleting a message doesn't make a channel less recently
        // active, and rewinding it would resurrect read channels in everyone's inbox.
        //
        // Every channel type, matching MessageCreatedHandler: the count is no longer forum-only.
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
