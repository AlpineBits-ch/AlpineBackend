using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Events.Role;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Bus.Events.Role;

public class RoleUpdatedHandler
{
    public static async Task Handle(RoleUpdated @event, MicroserviceContext ctx, GuildPermissionService guildPermissionService,
        IMessageBus bus)
    {
        var role = await ctx.Roles
            .Include(r => r.Members)
            .ThenInclude(m => m.Member)
            .FirstOrDefaultAsync(r => r.Id == @event.RoleId);
        if (role == null) return;

        foreach (var roleMember in role.Members)
        {
            await guildPermissionService.InvalidateUserPermissionsCacheAsync(@event.GuildId, roleMember.Member.UserId);
        }

        // For member removals role.Members no longer contains the removed member after the DB
        // commit, so the loop above misses them.
        if (@event.MemberId != null)
        {
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
        }
    }
}