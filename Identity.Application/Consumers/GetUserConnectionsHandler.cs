using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Resolves linked external accounts for the <c>connections</c> profile field (privacy spec T2-17).
///
/// <para>Steam is the only link type that exists here - <c>ApplicationUser.SteamId</c>, written by the
/// Steam link flow and cleared by <c>SteamUnlinkedEvent</c> and by <c>Tombstone()</c>. It is reported
/// as one entry in a typed list rather than as a <c>steamId</c> field so a second provider is an
/// additive change on this contract and on the profile DTO alike.</para>
///
/// <para><b>A raw SteamID64 is a cross-platform correlation handle</b>: it resolves to a public Steam
/// profile, a friend list and a play history, and it is stable forever. So this handler applies the
/// same viewer-independent floor as <see cref="GetUserBirthdaysHandler"/> - an account whose
/// <c>ConnectionsVisibility</c> is <c>Visibility.Nobody</c> answers with nothing, because there is no
/// viewer for whom answering could be correct. The per-viewer gate stays in Social's
/// <c>ProfileProjectionService</c>, which is the only place that knows the reader's relation to the
/// subject.</para>
///
/// <para>An account with no settings row at all answers with nothing, which is stricter than the
/// shipped <c>Friends</c> default. That asymmetry is intended: a row is minted by
/// <c>ApplicationUser.Create</c>/<c>CreateBot</c> and backfilled by migration, so its absence means
/// something unexpected happened, and the safe reading of "unexpected" is "do not hand out the
/// correlation handle".</para>
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
