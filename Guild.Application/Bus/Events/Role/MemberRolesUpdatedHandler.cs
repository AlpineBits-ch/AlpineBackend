using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Events.Role;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Bus.Events.Role;

/// <summary>
/// Invalidates one member's permission cache after a bulk role change and tells the bot gateway and
/// the guild's clients their role set moved.
/// </summary>
public class MemberRolesUpdatedHandler
{
    public static async Task Handle(MemberRolesUpdated @event, MicroserviceContext ctx,
        GuildPermissionService guildPermissionService, IMessageBus bus,
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate)
    {
        var userId = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.Id == @event.MemberId)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync();

        if (userId == null) return;

        await guildPermissionService.InvalidateUserPermissionsCacheAsync(@event.GuildId, userId);
        await bus.PublishAsync(new MemberUpdatedForBots { GuildId = @event.GuildId, UserId = userId });

        await RoleRealtime.BroadcastAsync(hub, hydrate, @event.GuildId, "guild.MemberRolesUpdated",
            new
            {
                GuildId = @event.GuildId,
                MemberId = @event.MemberId,
                UserId = userId,
                AddedRoleIds = @event.AddedRoleIds,
                RemovedRoleIds = @event.RemovedRoleIds,
            });
    }
}
