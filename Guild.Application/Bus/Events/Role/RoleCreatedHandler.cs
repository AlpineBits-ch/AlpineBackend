using Echo.Realtime;
using Facet.Extensions;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Events.Role;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using RoleAggregate = Guild.Domain.Aggregates.Role;

namespace Guild.Application.Bus.Events.Role;

/// <summary>Announces a new role to the guild's clients and to its installed bots.</summary>
public class RoleCreatedHandler
{
    public static async Task Handle(RoleCreated @event, MicroserviceContext ctx,
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate, IMessageBus bus)
    {
        var role = await ctx.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == @event.RoleId);
        if (role == null) return;

        await RoleRealtime.BroadcastAsync(hub, hydrate, @event.GuildId, "guild.RoleCreated",
            new { GuildId = @event.GuildId, Role = role.ToFacet<RoleAggregate, RoleDto>() });

        await bus.PublishAsync(new RoleCreatedForBots
        {
            GuildId = @event.GuildId,
            Role = RoleSnapshotMapper.From(role),
        });
    }
}
