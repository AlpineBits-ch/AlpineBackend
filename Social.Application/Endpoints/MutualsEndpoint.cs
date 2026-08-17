using System.Security.Claims;
using AppEnvironment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;
using Wolverine.Http;

namespace Social.Api.Endpoints;

/// <summary>
/// The mutual-friends and mutual-servers lists behind the full profile view. The profile payload
/// keeps embedding both as a preview; these serve the lists themselves, which a profile read must
/// not carry for a subject with a thousand friends.
/// </summary>
[Authorize]
public static class MutualsEndpoint
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    /// <summary>Guild answers the whole intersection at once, so this bounds the body rather than pages it.</summary>
    private const int MaxServers = 200;

    /// <summary>Friends the caller and <paramref name="profileId"/> have in common.</summary>
    [WolverineGet("/api/v1/profiles/{profileId}/mutual-friends")]
    public static async Task<IResult> MutualFriendsAsync(
        string profileId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] PrivacySettingsCache privacySettings,
        [NotBody] ClaimsPrincipal user,
        int? limit = null,
        string? cursor = null,
        CancellationToken token = default)
    {
        var gate = await ResolveAsync(ctx, privacySettings, user, profileId, forServers: false, token);
        if (gate.Refusal is not null) return gate.Refusal;
        if (gate.IsSelfView) return Results.Ok(new MutualFriendsPageDto());

        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var viewerFriendIds = ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == gate.ViewerProfileId && r.Status == RelationshipStatus.Friends)
            .Select(r => r.TargetId);

        var query = ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == profileId &&
                        r.Status == RelationshipStatus.Friends &&
                        viewerFriendIds.Contains(r.TargetId));

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!BlockEndpoints.TryDecodeCursor(cursor, out var curUpdatedAt, out var curId))
                return Results.BadRequest("Malformed cursor.");

            query = query.Where(r =>
                r.UpdatedAt < curUpdatedAt ||
                (r.UpdatedAt == curUpdatedAt && string.Compare(r.Id, curId) < 0));
        }

        var rows = await query
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new
            {
                r.Id,
                r.UpdatedAt,
                r.TargetId,
                r.Target.UserId,
                r.Target.UserName,
                r.Target.OnlineStatus,
            })
            .ToListAsync(token);

        var hasMore = rows.Count > pageSize;
        var page = hasMore ? rows.Take(pageSize).ToList() : rows;
        var last = page.LastOrDefault();

        return Results.Ok(new MutualFriendsPageDto
        {
            Items = page.Select(r => new MutualFriendRowDto
            {
                ProfileId = r.TargetId,
                UserId = r.UserId,
                UserName = r.UserName,
                AvatarUrl = $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/social/profiles/{r.TargetId}/avatar",
                // Friend, not Other: a mutual is by definition on the caller's own friend list.
                OnlineStatus = ProfileVisibility.ProjectStatus(r.OnlineStatus, ViewerRelation.Friend),
            }).ToList(),
            NextCursor = hasMore && last is not null ? BlockEndpoints.EncodeCursor(last.UpdatedAt, last.Id) : null,
        });
    }

    /// <summary>Guilds the caller and <paramref name="profileId"/> are both in.</summary>
    [WolverineGet("/api/v1/profiles/{profileId}/mutual-servers")]
    public static async Task<IResult> MutualServersAsync(
        string profileId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] PrivacySettingsCache privacySettings,
        [NotBody] ISharedGuildResolver sharedGuilds,
        [NotBody] ClaimsPrincipal user,
        CancellationToken token = default)
    {
        var gate = await ResolveAsync(ctx, privacySettings, user, profileId, forServers: true, token);
        if (gate.Refusal is not null) return gate.Refusal;
        if (gate.IsSelfView) return Results.Ok(new MutualServersPageDto());

        var shared = await sharedGuilds.SharedGuildsAsync(gate.ViewerUserId!, gate.SubjectUserId!, token);

        return Results.Ok(new MutualServersPageDto
        {
            Items = shared
                .Take(MaxServers)
                .Select(g => new MutualServerRowDto { GuildId = g.GuildId, Name = g.Name })
                .ToList(),
            NextCursor = null,
        });
    }

    private readonly record struct Gate(
        IResult? Refusal,
        string ViewerProfileId,
        string? ViewerUserId,
        string? SubjectUserId,
        bool IsSelfView = false);

    /// <summary>
    /// Resolves the caller, the subject and the one visibility setting that governs the route. The
    /// gate is the same <see cref="ProfileVisibility.CanView"/> the profile projection applies, so
    /// the two surfaces cannot disagree about who may see a list.
    /// </summary>
    private static async Task<Gate> ResolveAsync(
        MicroserviceContext ctx,
        PrivacySettingsCache privacySettings,
        ClaimsPrincipal user,
        string profileId,
        bool forServers,
        CancellationToken token)
    {
        var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (callerUserId is null) return new Gate(Results.Unauthorized(), string.Empty, null, null);

        var viewer = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == callerUserId, token);
        if (viewer is null) return new Gate(Results.BadRequest("Caller profile not found."), string.Empty, null, null);

        var subject = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, token);
        if (subject is null) return new Gate(Results.NotFound(), viewer.Id, viewer.UserId, null);

        // Nobody has mutuals with themselves, and answering the caller's own friend list here would
        // be a different endpoint wearing this one's gate.
        if (viewer.Id == subject.Id)
            return new Gate(null, viewer.Id, viewer.UserId, subject.UserId, IsSelfView: true);

        var relation = await ctx.ResolveViewerRelationAsync(viewer.Id, subject.Id);
        var settings = await privacySettings.GetAsync(subject.UserId, token);

        var visibility = forServers ? settings.MutualServersVisibility : settings.MutualFriendsVisibility;
        if (!ProfileVisibility.CanView(visibility, relation))
            return new Gate(Results.Json(new { code = "not_visible" }, statusCode: StatusCodes.Status403Forbidden),
                viewer.Id, viewer.UserId, subject.UserId);

        return new Gate(null, viewer.Id, viewer.UserId, subject.UserId);
    }
}
