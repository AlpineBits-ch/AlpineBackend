using Guild.Contracts.Bus.Request;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetGuildProfilesHandler
{
    // The caller is one page of discovery cards, never a bulk export - excess ids are dropped
    // rather than the request refused.
    private const int MaxGuildIds = 200;

    public static async Task<GetGuildProfilesResponse> Handle(
        GetGuildProfilesRequest request, MicroserviceContext ctx, CancellationToken ct)
    {
        var guildIds = request.GuildIds.Take(MaxGuildIds).ToList();

        // Features stays the raw enum through the query - ToString() on it does not translate to
        // SQL, so the string conversion happens after materialization instead.
        var rows = await ctx.Guilds
            .AsNoTracking()
            .Where(g => guildIds.Contains(g.Id))
            .Select(g => new
            {
                g.Id,
                g.Name,
                g.Features,
                MemberCount = g.Members.Count,
            })
            .ToListAsync(ct);

        var profiles = rows.Select(r => new GuildProfileDto
        {
            GuildId = r.Id,
            Name = r.Name,
            IconUrl = $"/api/v1/guilds/{r.Id}/icon",
            // Guild has no banner concept yet; guild_profile carries a nullable column for when it does.
            BannerUrl = null,
            MemberCount = r.MemberCount,
            // Guild does not track per-member activity, so this stands in for "active" until it does.
            ActiveMemberCount = r.MemberCount,
            Features = r.Features.ToString(),
        }).ToList();

        return new GetGuildProfilesResponse { Profiles = profiles };
    }
}
