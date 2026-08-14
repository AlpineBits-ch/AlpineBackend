using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Answers "which of these role ids may this message ping, and which need MentionEveryone" for a
/// channel - the Guild half of R5/R19 in docs/specs/guild-role-system-parity.md.
/// </summary>
public class ResolveRoleMentionsHandler
{
    public static async Task<ResolveRoleMentionsResponse> Handle(
        ResolveRoleMentionsRequest request,
        MicroserviceContext ctx)
    {
        if (request.RoleIds.Count == 0 || string.IsNullOrWhiteSpace(request.ChannelId))
            return new ResolveRoleMentionsResponse();

        var roleIds = request.RoleIds as IList<string> ?? request.RoleIds.ToList();

        var rows = await ctx.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id)
                        && ctx.Channels.Any(c => c.Id == request.ChannelId && c.GuildId == r.GuildId))
            .Select(r => new { r.Id, r.Mentionable })
            .ToListAsync();

        return new ResolveRoleMentionsResponse
        {
            MentionableRoleIds = rows.Where(r => r.Mentionable).Select(r => r.Id).ToList(),
            RestrictedRoleIds = rows.Where(r => !r.Mentionable).Select(r => r.Id).ToList(),
        };
    }
}
