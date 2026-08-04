using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Infrastructure.Persistence;

namespace Social.Api.Commands;

/// <summary>
/// Social's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command, MicroserviceContext ctx)
    {
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == command.UserId);

        if (profile is null)
        {
            return new ExportUserDataResponse
            {
                ExportId = command.ExportId,
                UserId = command.UserId,
                Service = "social",
                FragmentJson = JsonSerializer.Serialize(new { profile = (object?)null, relationships = Array.Empty<object>() }, UserDataExportJson.Options),
                RowCounts = new Dictionary<string, int> { ["profile"] = 0, ["relationships"] = 0 },
            };
        }

        // Owner-side only.
        var relationships = await ctx.Relationships
            .AsNoTracking()
            .Where(r => r.OwnerId == profile.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        var fragment = new
        {
            profile = new
            {
                profile.Id,
                profile.UserId,
                profile.UserName,
                profile.Bio,
                profile.AccentColor,
                font = profile.Font.ToString(),
                onlineStatus = profile.OnlineStatus.ToString(),
                profile.LastSeenAt,
                profile.FederatedServerId,
                profile.CreatedAt,
            },
            relationships = relationships.Select(r => new
            {
                r.Id,
                // The counterparty, as an opaque id.
                counterpartyProfileId = r.TargetId,
                status = r.Status.ToString(),
                r.OriginInstanceId,
                r.CreatedAt,
                r.UpdatedAt,
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "social",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["profile"] = 1,
                ["relationships"] = relationships.Count,
            },
        };
    }
}
