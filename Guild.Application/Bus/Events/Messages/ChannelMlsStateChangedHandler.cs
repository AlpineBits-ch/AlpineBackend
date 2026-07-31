using Echo.Realtime;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Guild.Application.Bus.Events.Messages;

/// <summary>
/// Fans a channel's encryption toggle out to the people in that channel.
///
/// <para>Messaging owns the MLS group but not channel membership, so it cannot address this audience
/// itself. This mirrors <see cref="MessageCreatedHandler"/> exactly - same channel-to-guild cache,
/// same presence lookup - rather than inventing a second way to answer "who is in this channel".</para>
///
/// <para>Clients have to act on this. One that keeps encrypting after a disable, or keeps sending
/// plaintext after an enable, has its sends refused until it catches up, so a missed notification
/// shows up as failed sends rather than as leaked plaintext.</para>
/// </summary>
public class ChannelMlsStateChangedHandler
{
    private static string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(
        ChannelMlsStateChanged message,
        IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService service,
        MicroserviceContext context,
        IDistributedCache cache,
        ILogger<ChannelMlsStateChangedHandler> logger)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var cachedGuildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
            var guildId = context.Channels
                .Where(c => c.Id == message.ChannelId)
                .Select(c => c.GuildId)
                .FirstOrDefault();

            if (guildId is null)
            {
                logger.LogWarning("Channel {ChannelId} not found while announcing MLS state change", message.ChannelId);
                return;
            }

            cachedGuildId = guildId;
            await cache.SetStringAsync(channelKey, guildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        // Including the user who flipped the switch: their other devices did not make the request
        // and still need to hear about it.
        await hub.Clients
            .Users(presence.Select(p => p.UserId))
            .SendAsync("guild.ChannelMlsStateChanged", new
            {
                channelId = message.ChannelId,
                guildId = cachedGuildId,
                encrypted = message.Encrypted,
                generation = message.Generation,
                changedByUserId = message.ChangedByUserId,
            });
    }
}
