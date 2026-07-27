using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class ThreadCreatedForBotsHandler
{
    public static Task Handle(ThreadCreatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ThreadDispatchHelper.DispatchAsync(ctx, registry, "THREAD_CREATE", evt.GuildId, evt.ChannelId, evt.ParentChannelId, evt.Name, archived: false);
}

public class ThreadUpdatedForBotsHandler
{
    public static Task Handle(ThreadUpdatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ThreadDispatchHelper.DispatchAsync(ctx, registry, "THREAD_UPDATE", evt.GuildId, evt.ChannelId, evt.ParentChannelId, evt.Name, evt.Archived);
}

file static class ThreadDispatchHelper
{
    public static async Task DispatchAsync(MicroserviceContext ctx, GatewayConnectionRegistry registry,
        string eventName, string guildId, string channelId, string parentChannelId, string name, bool archived)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, guildId);
        if (botUserIds.Count == 0) return;

        var payload = new ThreadPayload
        {
            Id = channelId,
            Type = DiscordChannelType.PublicThread,
            GuildId = guildId,
            ParentId = parentChannelId,
            Name = name,
            ThreadMetadata = new ThreadMetadataPayload { Archived = archived },
        };

        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, eventName, payload);
        }
    }
}
