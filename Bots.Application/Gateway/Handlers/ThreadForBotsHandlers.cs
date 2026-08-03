using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class ThreadCreatedForBotsHandler
{
    public static Task Handle(ThreadCreatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility) =>
        ThreadDispatchHelper.DispatchAsync(ctx, registry, visibility, "THREAD_CREATE", evt.GuildId, evt.ChannelId, evt.ParentChannelId, evt.Name, archived: false);
}

public class ThreadUpdatedForBotsHandler
{
    public static Task Handle(ThreadUpdatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility) =>
        ThreadDispatchHelper.DispatchAsync(ctx, registry, visibility, "THREAD_UPDATE", evt.GuildId, evt.ChannelId, evt.ParentChannelId, evt.Name, evt.Archived);
}

file static class ThreadDispatchHelper
{
    public static async Task DispatchAsync(MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility,
        string eventName, string guildId, string channelId, string parentChannelId, string name, bool archived)
    {
        // Filtered on the thread itself - threads inherit their parent's resolved permissions, so
        // a bot denied the parent is denied the thread.
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsForChannelAsync(ctx, visibility, guildId, channelId);
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
