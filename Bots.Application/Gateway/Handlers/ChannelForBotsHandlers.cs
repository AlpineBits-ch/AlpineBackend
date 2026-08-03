using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class ChannelCreatedForBotsHandler
{
    public static Task Handle(ChannelCreatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility) =>
        ChannelDispatchHelper.DispatchAsync(ctx, registry, visibility, "CHANNEL_CREATE", evt.GuildId, evt.ChannelId,
            new GatewayChannelPayload
            {
                Id = evt.ChannelId,
                GuildId = evt.GuildId,
                Name = evt.Name,
                Type = DiscordChannelType.FromEnumName(evt.Type),
                Position = evt.Position,
                ParentId = evt.CategoryId,
            });
}

public class ChannelUpdatedForBotsHandler
{
    public static Task Handle(ChannelUpdatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility) =>
        ChannelDispatchHelper.DispatchAsync(ctx, registry, visibility, "CHANNEL_UPDATE", evt.GuildId, evt.ChannelId,
            new GatewayChannelPayload
            {
                Id = evt.ChannelId,
                GuildId = evt.GuildId,
                Name = evt.Name,
                Type = DiscordChannelType.FromEnumName(evt.Type),
                Position = evt.Position,
                ParentId = evt.CategoryId,
            });
}

public class ChannelDeletedForBotsHandler
{
    // Deliberately unfiltered: the channel row is already gone, so a visibility check would
    // resolve to "deny everyone" and no bot would ever learn the channel was deleted. The delete
    // payload carries only the id and guild id - no name, topic or position - so it discloses
    // nothing about a channel the bot could not already see.
    public static Task Handle(ChannelDeletedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility) =>
        ChannelDispatchHelper.DispatchAsync(ctx, registry, visibility, "CHANNEL_DELETE", evt.GuildId, channelId: null,
            new GatewayChannelPayload { Id = evt.ChannelId, GuildId = evt.GuildId });
}

file static class ChannelDispatchHelper
{
    public static async Task DispatchAsync(MicroserviceContext ctx, GatewayConnectionRegistry registry,
        IBotChannelVisibility visibility,
        string eventName, string guildId, string? channelId, GatewayChannelPayload payload)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsForChannelAsync(ctx, visibility, guildId, channelId);
        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, eventName, payload);
        }
    }
}
