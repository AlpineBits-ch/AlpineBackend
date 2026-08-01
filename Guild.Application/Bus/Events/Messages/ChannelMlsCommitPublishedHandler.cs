using Echo.Realtime;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Guild.Application.Bus.Events.Messages;

/// <summary>Fans an encrypted channel's MLS commit out to the people in that channel.</summary>
public class ChannelMlsCommitPublishedHandler
{
    private static string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(
        ChannelMlsCommitPublished message,
        IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService service,
        MicroserviceContext context,
        IDistributedCache cache,
        ILogger<ChannelMlsCommitPublishedHandler> logger)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var cachedGuildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
            var guildId = await context.Channels
                .Where(c => c.Id == message.ChannelId)
                .Select(c => c.GuildId)
                .FirstOrDefaultAsync();

            if (guildId is null)
            {
                logger.LogWarning("Channel {ChannelId} not found while announcing an MLS commit", message.ChannelId);
                return;
            }

            cachedGuildId = guildId;
            await cache.SetStringAsync(channelKey, guildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        // Same event name as the conversation path, because it is the same instruction to the
        // client: fetch commits above your local epoch and apply them in order.
        await hub.Clients
            .Users(presence.Select(p => p.UserId))
            .SendAsync("conversation.MlsCommit", new
            {
                contextId = message.ChannelId,
                conversationId = (string?)null,
                channelId = message.ChannelId,
                guildId = cachedGuildId,
                generation = message.Generation,
                epoch = message.Epoch,
                senderDeviceId = message.SenderDeviceId,
                isProposal = message.IsProposal,
            });
    }
}
