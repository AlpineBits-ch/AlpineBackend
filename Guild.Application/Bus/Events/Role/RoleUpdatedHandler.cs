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

/// <summary>
/// Invalidates the permission caches a role edit affects, and tells clients and bots what changed.
/// </summary>
public class RoleUpdatedHandler
{
    public static async Task Handle(RoleUpdated @event, MicroserviceContext ctx, GuildPermissionService guildPermissionService,
        IMessageBus bus, IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate,
        PersonaService personas)
    {
        var role = await ctx.Roles
            .Include(r => r.Members)
            .ThenInclude(m => m.Member)
            .FirstOrDefaultAsync(r => r.Id == @event.RoleId);
        if (role == null) return;

        // Issued together rather than one at a time: this is one Redis DEL per holder, and a
        // permission edit on a role with a few thousand members was serialising a few thousand
        // round trips.
        await Task.WhenAll(role.Members
            .Select(roleMember => guildPermissionService.InvalidateUserPermissionsCacheAsync(
                @event.GuildId, roleMember.Member.UserId)));

        // For member removals role.Members no longer contains the removed member after the DB
        // commit, so the loop above misses them.
        if (@event.MemberId != null)
        {
            // A grant can name a role, so joining or leaving one changes which personas the member
            // may speak as. The send path re-reads grants, so this is the listing going stale for
            // up to the cache lifetime rather than a hole.
            await personas.InvalidateGuildAsync(@event.GuildId);

            var userId = await ctx.GuildMembers
                .AsNoTracking()
                .Where(m => m.Id == @event.MemberId)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync();

            if (userId != null)
            {
                await guildPermissionService.InvalidateUserPermissionsCacheAsync(@event.GuildId, userId);
                await bus.PublishAsync(new MemberUpdatedForBots { GuildId = @event.GuildId, UserId = userId });
            }

            // Which direction it went is not on the event - the same RoleUpdated is raised for an
            // add and for a remove - so it is read back off the committed membership rows, which
            // are already loaded above.
            var wasAdded = role.Members.Any(m => m.MemberId == @event.MemberId);

            await RoleRealtime.BroadcastAsync(hub, hydrate, @event.GuildId, "guild.MemberRolesUpdated",
                new
                {
                    GuildId = @event.GuildId,
                    MemberId = @event.MemberId,
                    UserId = userId,
                    AddedRoleIds = wasAdded ? new List<string> { @event.RoleId } : [],
                    RemovedRoleIds = wasAdded ? [] : new List<string> { @event.RoleId },
                });

            return;
        }

        await RoleRealtime.BroadcastAsync(hub, hydrate, @event.GuildId, "guild.RoleUpdated",
            new { GuildId = @event.GuildId, Role = role.ToFacet<RoleAggregate, RoleDto>() });

        await bus.PublishAsync(new RoleUpdatedForBots
        {
            GuildId = @event.GuildId,
            Role = RoleSnapshotMapper.From(role),
        });
    }
}
