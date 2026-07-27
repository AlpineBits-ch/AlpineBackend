using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetGuildMemberHandler
{
    public static async Task<GetGuildMemberResponse> Handle(GetGuildMemberRequest request, MicroserviceContext ctx)
    {
        var member = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == request.GuildId && m.UserId == request.UserId)
            .Select(m => new GuildMemberSummary
            {
                UserId = m.UserId,
                Nickname = m.Nickname,
                RoleIds = m.RoleMembers.Select(rm => rm.RoleId).ToList(),
                JoinedAt = m.JoinedAt,
                IsBot = m.Type == MemberType.Bot,
                MutedUntil = m.MutedUntil,
            })
            .FirstOrDefaultAsync();

        return new GetGuildMemberResponse { Member = member };
    }
}
