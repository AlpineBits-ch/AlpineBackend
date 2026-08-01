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
/// Fans an encrypted channel's MLS commit out to the people in that channel.
///
/// <para>Messaging owns the group but not channel membership, so it cannot address this audience
/// itself. This mirrors <see cref="ChannelMlsStateChangedHandler"/> exactly - same channel-to-guild
/// cache, same presence lookup - rather than inventing a second way to answer "who is in this
/// channel".</para>
///
/// <para>The channel path previously announced commits to nobody at all, so every member except the
/// publisher stayed on the old epoch and silently stopped being able to read the channel. A missed
/// push is only a round-trip, because the client fetches the ordered commit list rather than
/// applying anything carried here.</para>
/// </summary>
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
        // client: fetch commits above your local epoch and apply them in order. Clients switch on
        // contextId - a channel commit says nothing about conversation membership.
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
