using Guild.Application.Services;
using Guild.Domain.Events.Permission;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Events.Permission;

public class ChannelPermissionChangedHandler
{
    public static async Task Handle(ChannelPermissionChanged @event, MicroserviceContext ctx, GuildPermissionService guildPermissionService)
    {
        if (@event.MemberId != null)
        {
            var userId = await ctx.GuildMembers
                .AsNoTracking()
                .Where(m => m.Id == @event.MemberId)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync();

            if (userId != null)
                await guildPermissionService.InvalidateUserPermissionsCacheAsync(@event.GuildId, userId);

            return;
        }

        if (@event.RoleId != null)
        {
            var userIds = await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => rm.RoleId == @event.RoleId)
                .Join(ctx.GuildMembers.AsNoTracking(), rm => rm.MemberId, m => m.Id, (rm, m) => m.UserId)
                .ToListAsync();

            foreach (var userId in userIds)
                await guildPermissionService.InvalidateUserPermissionsCacheAsync(@event.GuildId, userId);
        }
    }
}
