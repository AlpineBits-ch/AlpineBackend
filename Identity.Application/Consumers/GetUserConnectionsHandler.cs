using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Resolves linked external accounts for the <c>connections</c> profile field (privacy spec T2-17).
/// </summary>
public class GetUserConnectionsHandler
{
    public static async Task<GetUserConnectionsResponse> Handle(
        GetUserConnectionsRequest request,
        MicroserviceContext ctx)
    {
        var userIds = request.UserIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new GetUserConnectionsResponse();

        var rows = await ctx.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new
            {
                u.Id,
                u.SteamId,
                // See GetUserBirthdaysHandler for why this is an explicit null check and not a
                // dereference of the optional navigation.
                Visibility = u.UserPrivacySettings == null
                    ? (Visibility?)null
                    : u.UserPrivacySettings.ConnectionsVisibility,
            })
            .ToListAsync();

        var byId = rows.ToDictionary(r => r.Id);

        var users = userIds.Select(id =>
        {
            var summary = new UserConnectionsSummary { UserId = id };

            if (!byId.TryGetValue(id, out var row)) return summary;
            if (row.Visibility != Visibility.Everyone && row.Visibility != Visibility.Friends) return summary;

            if (!string.IsNullOrWhiteSpace(row.SteamId))
            {
                summary.Connections.Add(new ExternalConnectionSummary
                {
                    Type = ExternalConnectionTypes.Steam,
                    ExternalId = row.SteamId,
                    // Nothing in this codebase stores or fetches a Steam persona name.
                    DisplayName = null,
                });
            }

            return summary;
        }).ToList();

        return new GetUserConnectionsResponse { Users = users };
    }
}
