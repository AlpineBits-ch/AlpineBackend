using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class ListGuildMembersHandler
{
    public static async Task<ListGuildMembersResponse> Handle(ListGuildMembersRequest request, MicroserviceContext ctx)
    {
        var members = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == request.GuildId)
            .Take(Math.Clamp(request.Limit, 1, 1000))
            .Select(m => new GuildMemberSummary
            {
                UserId = m.UserId,
                Nickname = m.Nickname,
                RoleIds = m.RoleMembers.Select(rm => rm.RoleId).ToList(),
                JoinedAt = m.JoinedAt,
                IsBot = m.Type == MemberType.Bot,
                MutedUntil = m.MutedUntil,
            })
            .ToListAsync();

        return new ListGuildMembersResponse { Members = members };
    }
}
