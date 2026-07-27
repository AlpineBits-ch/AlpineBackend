using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class ListManageableGuildsForUserHandler
{
    public static async Task<ListManageableGuildsForUserResponse> Handle(
        ListManageableGuildsForUserRequest request, MicroserviceContext ctx, GuildPermissionService permissionService)
    {
        var memberOfGuildIds = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == request.UserId)
            .Select(m => m.GuildId)
            .Distinct()
            .ToListAsync();

        if (memberOfGuildIds.Count == 0) return new ListManageableGuildsForUserResponse();

        var candidates = await ctx.Guilds
            .AsNoTracking()
            .Where(g => memberOfGuildIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name })
            .ToListAsync();

        var manageable = new List<ManageableGuildSummary>();
        foreach (var guild in candidates)
        {
            var hasManageGuild = await permissionService.CanUserPerformActionOnGuildAsync(
                request.UserId, guild.Id, Permissions.ManageGuild);
            if (hasManageGuild)
            {
                manageable.Add(new ManageableGuildSummary { Id = guild.Id, Name = guild.Name });
            }
        }

        return new ListManageableGuildsForUserResponse { Guilds = manageable };
    }
}
