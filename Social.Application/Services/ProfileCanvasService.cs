using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>Reads, writes and per-viewer projection for the profile canvas.</summary>
public sealed class ProfileCanvasService(MicroserviceContext ctx, ISharedGuildResolver sharedGuilds)
{
    public Task<ProfileCanvas?> FindAsync(string profileId, CancellationToken token = default)
        => ctx.ProfileCanvases.AsNoTracking().FirstOrDefaultAsync(c => c.ProfileId == profileId, token);

    /// <summary>Rehydrates the stored jsonb into the shape the API speaks.</summary>
    public static ProfileCanvasDto ToDto(ProfileCanvas row) => new()
    {
        ProfileId = row.ProfileId,
        UpdatedAt = row.UpdatedAt,
        Version = row.Version,
        Theme = JsonSerializer.Deserialize<CanvasThemeDto>(row.ThemeJson, CanvasJson.Options) ?? new CanvasThemeDto(),
        Widgets = JsonSerializer.Deserialize<List<CanvasWidgetDto>>(row.WidgetsJson, CanvasJson.Options) ?? [],
    };

    /// <summary>Upserts the owner's canvas and bumps the version. Validate before calling.</summary>
    public async Task<ProfileCanvasDto> SaveAsync(string profileId, CanvasWriteDto write, CancellationToken token = default)
    {
        var themeJson = JsonSerializer.Serialize(write.Theme ?? new CanvasThemeDto(), CanvasJson.Options);
        var widgetsJson = JsonSerializer.Serialize(write.Widgets ?? [], CanvasJson.Options);

        var row = await ctx.ProfileCanvases.FirstOrDefaultAsync(c => c.ProfileId == profileId, token);
        if (row is null)
        {
            row = ProfileCanvas.Create(profileId, themeJson, widgetsJson);
            ctx.ProfileCanvases.Add(row);
        }
        else
        {
            row.ThemeJson = themeJson;
            row.WidgetsJson = widgetsJson;
            row.Version++;
        }

        await ctx.SaveChangesAsync(token);

        return ToDto(row);
    }

    /// <summary>
    /// Classifies <paramref name="viewerProfileId"/> against the owner. The mutual lookup costs a
    /// bus hop, so it only runs when a widget actually gates on it.
    /// </summary>
    public async Task<CanvasViewer> ResolveViewerAsync(
        Profile viewer,
        Profile owner,
        IReadOnlyList<CanvasWidgetDto> widgets,
        CancellationToken token = default)
    {
        if (viewer.Id == owner.Id) return CanvasViewer.Owner;

        var isFriend = await ctx.AreFriendsAsync(viewer.Id, owner.Id);

        if (!CanvasVisibility.NeedsMutualLookup(widgets))
            return new CanvasViewer(false, isFriend, false);

        var isMutual = await ctx.HasMutualFriendAsync(viewer.Id, owner.Id)
                       || await sharedGuilds.ShareAnyGuildAsync(viewer.UserId, owner.UserId, token);

        return new CanvasViewer(false, isFriend, isMutual);
    }

    /// <summary>The owner's accepted friends, the audience the realtime fan-out reaches.</summary>
    public async Task<List<CanvasRecipient>> FriendsOfAsync(
        string ownerProfileId, int limit, CancellationToken token = default)
    {
        var rows = await ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == ownerProfileId && r.Status == RelationshipStatus.Friends)
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .Select(r => new { r.TargetId, r.Target.UserId })
            .ToListAsync(token);

        return rows.Select(r => new CanvasRecipient(r.TargetId, r.UserId)).ToList();
    }

    /// <summary>
    /// Which of <paramref name="candidates"/> count as mutuals of the owner, resolved for the whole
    /// list at once: one relationship query plus one shared-guild round trip.
    /// </summary>
    public async Task<IReadOnlySet<string>> MutualsAmongAsync(
        Profile owner, IReadOnlyList<CanvasRecipient> candidates, CancellationToken token = default)
    {
        if (candidates.Count == 0) return new HashSet<string>();

        var candidateProfileIds = candidates.Select(c => c.ProfileId).Distinct().ToList();

        // A friend of the owner who is also friends with another friend of the owner has a friend
        // in common with them, which is one half of the mutual test.
        var withMutualFriend = (await ctx.Relationships.AsNoTracking()
            .Where(r => r.Status == RelationshipStatus.Friends &&
                        candidateProfileIds.Contains(r.OwnerId) &&
                        candidateProfileIds.Contains(r.TargetId))
            .Select(r => r.OwnerId)
            .Distinct()
            .ToListAsync(token)).ToHashSet();

        var sharing = await sharedGuilds.ShareAnyGuildAsync(
            owner.UserId, candidates.Select(c => c.UserId).ToList(), token);

        var mutuals = candidates
            .Where(c => withMutualFriend.Contains(c.ProfileId) || sharing.Contains(c.UserId))
            .Select(c => c.ProfileId)
            .ToHashSet();

        return mutuals;
    }
}

/// <summary>One person the canvas fan-out addresses.</summary>
public readonly record struct CanvasRecipient(string ProfileId, string UserId);
