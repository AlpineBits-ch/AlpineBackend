using Echo.Realtime;

using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Bus.Events.Messages;

public class ReactionHandler
{

    private async Task<IReadOnlyList<MemberPresenceState>> GetPresenceByChannel(string channelId, IDistributedCache cache, MicroserviceContext context, ILogger<MessageCreatedHandler> logger, GuildHydrateService service)
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
                return [];
            }
            guildId = channel;
            cachedGuildId = guildId;
            await cache.SetStringAsync(channelKey, guildId);
        }

        return await service.GetGuildPresenceAsync(cachedGuildId);
    }
    
    private string GetChannelKey(string channelId)
    {
        return $"channel:{channelId}:guild";
    }
    public async Task Handle(ReactionCreatedEvent reactionCreatedEvent, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, ILogger<MessageCreatedHandler> logger)
    {
        var presence = await GetPresenceByChannel(reactionCreatedEvent.ChannelId, cache, context, logger, service);

        var users = presence.Select(p => p.UserId).Where(u => u != reactionCreatedEvent.UserId);
        await hub.Clients.Users(users).SendAsync("guild.ReactionCreated", reactionCreatedEvent);
    }
    
    public async Task Handle(ReactionRemovedEvent reactionRemovedEvent, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, ILogger<MessageCreatedHandler> logger)
    {
        var presence = await GetPresenceByChannel(reactionRemovedEvent.ChannelId, cache, context, logger, service);
        var users = presence.Select(p => p.UserId).Where(u => u != reactionRemovedEvent.UserId);
        await hub.Clients.Users(users).SendAsync("guild.ReactionRemoved", reactionRemovedEvent);
    }
}