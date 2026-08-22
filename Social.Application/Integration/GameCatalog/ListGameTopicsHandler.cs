using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.GameCatalog;

public class ListGameTopicsHandler
{
    private const int MaxLimit = 1000;

    public static async Task<ListGameTopicsResponse> Handle(
        ListGameTopicsRequest request,
        MicroserviceContext ctx,
        CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);

        var query = ctx.GameApplications.AsNoTracking().OrderBy(g => g.Id).AsQueryable();
        if (!string.IsNullOrEmpty(request.After))
            query = query.Where(g => string.Compare(g.Id, request.After) > 0);

        var rows = await query.Take(limit + 1).Select(g => new GameTopicDto
        {
            Id = g.Id,
            Name = g.Name,
            Aliases = g.Aliases,
            SteamAppId = g.SteamAppId,
            IsEnabled = g.IsEnabled,
        }).ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new ListGameTopicsResponse
        {
            Topics = rows,
            NextCursor = hasMore ? rows[^1].Id : null,
        };
    }
}
