using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Guild.Application.Bus.Consumers;

public class GetGuildProfilesHandler
{
    // The caller is one page of discovery cards, never a bulk export - excess ids are dropped
    // rather than the request refused.
    private const int MaxGuildIds = 200;

    public static async Task<GetGuildProfilesResponse> Handle(
        GetGuildProfilesRequest request,
        MicroserviceContext ctx,
        GuildHydrateService presence,
        ILogger<GetGuildProfilesHandler> logger,
        CancellationToken ct)
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

        var onlineCounts = await OnlineCountsAsync(guildIds, presence, logger);

        var profiles = rows.Select(r => new GuildProfileDto
        {
            GuildId = r.Id,
            Name = r.Name,
            IconUrl = $"/api/v1/guilds/{r.Id}/icon",
            // Guild has no banner concept yet; guild_profile carries a nullable column for when it does.
            BannerUrl = null,
            MemberCount = r.MemberCount,
            // Online-now from presence, not a 14-day activity figure - Guild has no activity
            // history to give one.
            ActiveMemberCount = onlineCounts.GetValueOrDefault(r.Id, 0),
            Features = r.Features.ToString(),
        }).ToList();

        return new GetGuildProfilesResponse { Profiles = profiles };
    }

    // One presence lookup per guild rather than a batch call - GuildHydrateService has no batch
    // API, and a Redis miss on one guild must not take the others down with it.
    private static async Task<Dictionary<string, int>> OnlineCountsAsync(
        IReadOnlyList<string> guildIds, GuildHydrateService presence, ILogger logger)
    {
        var pairs = await Task.WhenAll(guildIds.Select(async guildId =>
        {
            try
            {
                var online = await presence.GetGuildPresenceAsync(guildId);
                return (guildId, count: online.Count);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Could not read presence for guild {GuildId}, treating it as zero online", guildId);
                return (guildId, count: 0);
            }
        }));

        return pairs.ToDictionary(p => p.guildId, p => p.count);
    }
}
