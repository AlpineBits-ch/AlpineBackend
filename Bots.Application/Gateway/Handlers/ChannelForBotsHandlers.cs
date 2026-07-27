using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

public class ChannelCreatedForBotsHandler
{
    public static Task Handle(ChannelCreatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ChannelDispatchHelper.DispatchAsync(ctx, registry, "CHANNEL_CREATE", evt.GuildId,
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
    public static Task Handle(ChannelUpdatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ChannelDispatchHelper.DispatchAsync(ctx, registry, "CHANNEL_UPDATE", evt.GuildId,
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
    public static Task Handle(ChannelDeletedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        ChannelDispatchHelper.DispatchAsync(ctx, registry, "CHANNEL_DELETE", evt.GuildId,
            new GatewayChannelPayload { Id = evt.ChannelId, GuildId = evt.GuildId });
}

file static class ChannelDispatchHelper
{
    public static async Task DispatchAsync(MicroserviceContext ctx, GatewayConnectionRegistry registry,
        string eventName, string guildId, GatewayChannelPayload payload)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, guildId);
        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, eventName, payload);
        }
    }
}
