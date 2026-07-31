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
/// Tells a channel's members that somebody wants in.
///
/// <para>Carries only the channel and who is asking. The fingerprint a reviewer compares is
/// deliberately not broadcast: it is fetched with the request over an authenticated read, so it
/// reaches people who can actually see the channel rather than everyone in the guild's presence
/// set.</para>
/// </summary>
public class ChannelMlsJoinRequestedHandler
{
    private static string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(
        ChannelMlsJoinRequested message,
        IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService service,
        MicroserviceContext context,
        IDistributedCache cache,
        ILogger<ChannelMlsJoinRequestedHandler> logger)
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
                logger.LogWarning("Channel {ChannelId} not found while announcing an MLS join request", message.ChannelId);
                return;
            }

            cachedGuildId = guildId;
            await cache.SetStringAsync(channelKey, guildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        await hub.Clients
            .Users(presence.Select(p => p.UserId).Except([message.RequesterUserId]))
            .SendAsync("guild.ChannelMlsJoinRequested", new
            {
                channelId = message.ChannelId,
                guildId = cachedGuildId,
                requesterUserId = message.RequesterUserId,
            });
    }
}
