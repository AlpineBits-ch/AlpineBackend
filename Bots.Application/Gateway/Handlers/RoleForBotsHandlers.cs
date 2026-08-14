using Bots.Contracts.Gateway.Payloads;
using Bots.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway.Handlers;

/// <summary>
/// Translates Guild's role lifecycle into Discord's GUILD_ROLE_CREATE / GUILD_ROLE_UPDATE /
/// GUILD_ROLE_DELETE gateway dispatches.
/// </summary>
public class RoleCreatedForBotsHandler
{
    public static Task Handle(RoleCreatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        RoleDispatch.SendAsync(ctx, registry, "GUILD_ROLE_CREATE", evt.GuildId,
            new GuildRoleUpdatePayload { GuildId = evt.GuildId, Role = GatewayRoleMapper.ToPayload(evt.Role) });
}

public class RoleUpdatedForBotsHandler
{
    public static Task Handle(RoleUpdatedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        RoleDispatch.SendAsync(ctx, registry, "GUILD_ROLE_UPDATE", evt.GuildId,
            new GuildRoleUpdatePayload { GuildId = evt.GuildId, Role = GatewayRoleMapper.ToPayload(evt.Role) });
}

public class RoleDeletedForBotsHandler
{
    public static Task Handle(RoleDeletedForBots evt, MicroserviceContext ctx, GatewayConnectionRegistry registry) =>
        RoleDispatch.SendAsync(ctx, registry, "GUILD_ROLE_DELETE", evt.GuildId,
            new GuildRoleDeletePayload { GuildId = evt.GuildId, RoleId = evt.RoleId });
}

file static class RoleDispatch
{
    public static async Task SendAsync(MicroserviceContext ctx, GatewayConnectionRegistry registry,
        string eventName, string guildId, object payload)
    {
        var botUserIds = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(ctx, guildId);
        foreach (var botUserId in botUserIds)
        {
            await registry.PublishAsync(botUserId, eventName, payload);
        }
    }
}
