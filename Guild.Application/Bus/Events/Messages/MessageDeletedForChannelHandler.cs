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

        // Keeps the forum post card's reply count honest. Clamped at zero rather than trusting the
        // counter: it's a denormalized best-effort tally over bus events, so a delete whose create
        // was never seen would otherwise drive it negative. LastActivityAt deliberately isn't
        // rewound - deleting a message doesn't make the post less recently active.
        var thread = await context.Channels
            .FirstOrDefaultAsync(c => c.Id == message.ChannelId && c.Type == ChannelType.Thread);

        if (thread is not null && thread.MessageCount > 0) thread.MessageCount--;
    }
}
