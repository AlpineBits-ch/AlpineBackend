using Echo.Realtime;

using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Messages;

public class ReactionHandler
{

    private async Task<(IReadOnlyList<MemberPresenceState> Presence, string GuildId)> GetPresenceByChannel(string channelId, IDistributedCache cache, MicroserviceContext context, ILogger<MessageCreatedHandler> logger, GuildHydrateService service)
    {
        var channelKey = GetChannelKey(channelId);
        var cachedGuildId = await cache.GetStringAsync(channelKey);
        string guildId;
        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
            var channel =  context.Channels.Where(c => c.Id == channelId).Select(c => c.GuildId).FirstOrDefault();
            if (channel is null)
            {
                logger.LogWarning($"Channel with ID {channelId} not found in context");
                return ([], "");
            }
            guildId = channel;
            cachedGuildId = guildId;
            await cache.SetStringAsync(channelKey, guildId);
        }

        return (await service.GetGuildPresenceAsync(cachedGuildId), cachedGuildId);
    }

    private string GetChannelKey(string channelId)
    {
        return $"channel:{channelId}:guild";
    }
    public async Task Handle(ReactionCreatedEvent reactionCreatedEvent, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, ILogger<MessageCreatedHandler> logger, IMessageBus bus)
    {
        var (presence, guildId) = await GetPresenceByChannel(reactionCreatedEvent.ChannelId, cache, context, logger, service);
        if (string.IsNullOrEmpty(guildId)) return;

        var users = presence.Select(p => p.UserId).Where(u => u != reactionCreatedEvent.UserId);
        await hub.Clients.Users(users).SendAsync("guild.ReactionCreated", reactionCreatedEvent);

        await bus.PublishAsync(new ReactionCreatedForBots
        {
            GuildId = guildId,
            ChannelId = reactionCreatedEvent.ChannelId,
            MessageId = reactionCreatedEvent.MessageId,
            Emoji = reactionCreatedEvent.Emoji,
            UserId = reactionCreatedEvent.UserId,
        });
    }

    public async Task Handle(ReactionRemovedEvent reactionRemovedEvent, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, ILogger<MessageCreatedHandler> logger, IMessageBus bus)
    {
        var (presence, guildId) = await GetPresenceByChannel(reactionRemovedEvent.ChannelId, cache, context, logger, service);
        if (string.IsNullOrEmpty(guildId)) return;

        var users = presence.Select(p => p.UserId).Where(u => u != reactionRemovedEvent.UserId);
        await hub.Clients.Users(users).SendAsync("guild.ReactionRemoved", reactionRemovedEvent);

        await bus.PublishAsync(new ReactionRemovedForBots
        {
            GuildId = guildId,
            ChannelId = reactionRemovedEvent.ChannelId,
            MessageId = reactionRemovedEvent.MessageId,
            Emoji = reactionRemovedEvent.Emoji,
            UserId = reactionRemovedEvent.UserId,
        });
    }
}