using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetGuildSnapshotForBotHandler
{
    public static async Task<GetGuildSnapshotForBotResponse> Handle(GetGuildSnapshotForBotRequest request, MicroserviceContext ctx)
    {
        var guild = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == request.GuildId)
            .Select(g => new { g.Id, g.Name, g.OwnerId })
            .FirstOrDefaultAsync();

        if (guild is null) return new GetGuildSnapshotForBotResponse { Guild = null };

        var channels = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.GuildId == request.GuildId)
            .Select(c => new ChannelSnapshot
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type.ToString(),
                Position = c.Position,
                CategoryId = c.CategoryId,
            })
            .ToListAsync();

        var roles = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == request.GuildId)
            .Select(r => new RoleSnapshot
            {
                Id = r.Id,
                Name = r.Name,
                Color = r.Color,
                Position = r.Position,
                Permissions = (ulong)r.Permissions,
            })
            .ToListAsync();

        var self = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == request.GuildId && m.UserId == request.BotUserId)
            .Select(m => new GuildMemberSummary
            {
                UserId = m.UserId,
                Nickname = m.Nickname,
                RoleIds = m.RoleMembers.Select(rm => rm.RoleId).ToList(),
                JoinedAt = m.JoinedAt,
                IsBot = m.Type == MemberType.Bot,
            })
            .FirstOrDefaultAsync();

        return new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot
            {
                Id = guild.Id,
                Name = guild.Name,
                OwnerId = guild.OwnerId,
                Channels = channels,
                Roles = roles,
                Self = self,
            },
        };
    }
}
