using System.Text.Json;
using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Api.Services;
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
                FragmentJson = JsonSerializer.Serialize(
                    new { profile = (object?)null, relationships = Array.Empty<object>(), canvas = (object?)null, canvasImages = Array.Empty<object>() },
                    UserDataExportJson.Options),
                RowCounts = new Dictionary<string, int> { ["profile"] = 0, ["relationships"] = 0, ["canvas"] = 0, ["canvasImages"] = 0 },
            };
        }

        // Owner-side only.
        var relationships = await ctx.Relationships
            .AsNoTracking()
            .Where(r => r.OwnerId == profile.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        var canvasRow = await ctx.ProfileCanvases
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProfileId == profile.Id);

        var canvasImages = await ctx.ProfileCanvasImages
            .AsNoTracking()
            .Where(i => i.ProfileId == profile.Id)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync();

        // This is the owner reading their own data, not the GET route: nothing is stripped by
        // widget visibility the way CanvasVisibility.Strip would for another viewer.
        var canvasDto = canvasRow is null ? null : ProfileCanvasService.ToDto(canvasRow);

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
            canvas = canvasDto is null ? null : new
            {
                canvasDto.Version,
                canvasDto.UpdatedAt,
                canvasDto.Theme,
                canvasDto.Widgets,
            },
            canvasImages = canvasImages.Select(i => new
            {
                i.Id,
                i.ContentType,
                i.SizeBytes,
                url = CanvasImageUrl(i.Id),
                i.CreatedAt,
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
                ["canvas"] = canvasRow is null ? 0 : 1,
                ["canvasImages"] = canvasImages.Count,
            },
        };
    }

    // Same durable route ProfileCanvasController.ImageUrl builds, not a presigned URL: an export is
    // read long after the window a presigned link is valid for.
    private static string CanvasImageUrl(string imageId) =>
        $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/social/canvas-images/{imageId}";
}
