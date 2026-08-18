using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Events.Role;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Guild.Application.Bus.Events.Role;

/// <summary>
/// Clears the permission cache of everyone who held a deleted role, and tells clients and bots the
/// role is gone.
/// </summary>
public class RoleDeletedHandler
{
    public static async Task Handle(RoleDeleted @event, GuildPermissionService guildPermissionService,
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate, IMessageBus bus,
        PersonaService personas)
    {
        // Any persona grant naming this role cascaded away with it.
        await personas.InvalidateGuildAsync(@event.GuildId);

        if (@event.UserIds.Count > 0)
        {
            await Task.WhenAll(@event.UserIds.Distinct()
                .Select(userId => guildPermissionService.InvalidateUserPermissionsCacheAsync(@event.GuildId, userId)));
        }

        await RoleRealtime.BroadcastAsync(hub, hydrate, @event.GuildId, "guild.RoleDeleted",
            new { GuildId = @event.GuildId, RoleId = @event.RoleId });

        await bus.PublishAsync(new RoleDeletedForBots { GuildId = @event.GuildId, RoleId = @event.RoleId });
    }
}
