using System.Security.Claims;
using Echo.Realtime;
using FluentValidation;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The scene archive's shelves and labels. Filing is structure and needs ManageScenes; tagging is
/// description and does not, unless the tag itself is moderated.
/// </summary>
[Authorize]
public class SceneTaxonomyEndpoint
{
    public const string TaxonomyEvent = "guild.SceneTaxonomyChanged";

    // ══════════════════════════════════════════════════════════════════════ Read
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Folders and tags in one call. They are always read together, replaced together by
    /// <see cref="TaxonomyEvent"/>, and small enough that splitting them only costs a round trip.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/scene-taxonomy")]
    public async Task<IResult> GetTaxonomyAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckMembershipAsync(
                permissionService, ctx, guildId, userId, GuildFeatures.Scenes) is { } denied)
        {
            return denied;
        }

        return Results.Ok(await ReadTaxonomyAsync(ctx, guildId));
    }

    // ══════════════════════════════════════════════════════════════════════ Folders
    // ══════════════════════════════════════════════════════════════════════

    [WolverinePost("/api/v1/guilds/{guildId}/scene-folders")]
    public async Task<IResult> CreateFolderAsync(string guildId, CreateSceneFolderDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var parentId = Blank(dto.ParentFolderId);
        if (parentId is not null)
        {
            var parent = await ctx.SceneFolders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == parentId && f.GuildId == guildId);

            if (parent is null)
                return Fault("scene_folder_not_found", "That parent folder does not exist in this guild.");

            if (parent.ParentFolderId is not null)
                return Fault("scene_folder_depth_exceeded",
                    $"Folders nest {SceneFolder.MaxDepth} deep. Use tags for anything that cuts across.");
        }

        var siblings = await ctx.SceneFolders
            .Where(f => f.GuildId == guildId)
            .ToListAsync();

        SceneFolder folder;
        try
        {
            folder = SceneFolder.Create(new CreateSceneFolderParams
            {
                GuildId = guildId,
                Name = dto.Name,
                ParentFolderId = parentId,
                Icon = dto.Icon,
                Color = dto.Color,
                // Appended; ordering is the reorder route's job.
                Position = siblings.Count == 0 ? 0 : siblings.Max(f => f.Position) + 1,
            });
        }
        catch (ValidationException validationException)
        {
            return ValidationProblem(validationException);
        }

        ctx.SceneFolders.Add(folder);

        auditLog.Log(guildId, userId, AuditActionType.SceneFolderCreated, folder.Id, new { folder.Name });

        await BroadcastAsync(hub, hydrate, ctx, guildId, folder);

        return Results.Ok(SceneFolderDto.From(folder));
    }

    [WolverinePatch("/api/v1/scene-folders/{folderId}")]
    public async Task<IResult> UpdateFolderAsync(string folderId, UpdateSceneFolderDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var folder = await ctx.SceneFolders.FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder is null) return Results.NotFound();

        if (await Gate(permissionService, ctx, folder.GuildId, userId) is { } denied) return denied;

        if (dto.ParentFolderId is not null)
        {
            var moveTo = Blank(dto.ParentFolderId);
            if (moveTo is not null)
            {
                if (moveTo == folder.Id)
                    return Fault("scene_folder_cycle", "A folder cannot be filed inside itself.");

                var parent = await ctx.SceneFolders.AsNoTracking()
                    .FirstOrDefaultAsync(f => f.Id == moveTo && f.GuildId == folder.GuildId);

                if (parent is null)
                    return Fault("scene_folder_not_found", "That parent folder does not exist in this guild.");

                if (parent.ParentFolderId is not null)
                    return Fault("scene_folder_depth_exceeded",
                        $"Folders nest {SceneFolder.MaxDepth} deep. Use tags for anything that cuts across.");

                // Only a folder with no children may become a child, or the move would push its own
                // children to depth three without ever naming them.
                if (await ctx.SceneFolders.AnyAsync(f => f.ParentFolderId == folder.Id))
                    return Fault("scene_folder_depth_exceeded",
                        "Move the child folders out first: this one holds folders of its own.");
            }
        }

        try
        {
            folder.Update(new SceneFolder.UpdateSceneFolderParams
            {
                Name = dto.Name,
                ParentFolderId = dto.ParentFolderId,
                Icon = dto.Icon,
                Color = dto.Color,
            });
        }
        catch (ValidationException validationException)
        {
            return ValidationProblem(validationException);
        }

        folder.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(folder.GuildId, userId, AuditActionType.SceneFolderUpdated, folder.Id, new { folder.Name });

        await BroadcastAsync(hub, hydrate, ctx, folder.GuildId, folder);

        return Results.Ok(SceneFolderDto.From(folder));
    }

    /// <summary>
    /// Removes a shelf, never what is on it: children come back to the root and the scenes go
    /// unfiled.
    /// </summary>
    [WolverineDelete("/api/v1/scene-folders/{folderId}")]
    public async Task<IResult> DeleteFolderAsync(string folderId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var folder = await ctx.SceneFolders.FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder is null) return Results.NotFound();

        if (await Gate(permissionService, ctx, folder.GuildId, userId) is { } denied) return denied;

        // Postgres would SetNull both of these, but the InMemory provider the unit tests use does
        // not, and leaving it implicit would let the two disagree about what a delete leaves behind.
        var children = await ctx.SceneFolders.Where(f => f.ParentFolderId == folderId).ToListAsync();
        foreach (var child in children) child.ParentFolderId = null;

        var filed = await ctx.SceneStates.Where(s => s.FolderId == folderId).ToListAsync();
        foreach (var scene in filed) scene.FolderId = null;

        ctx.SceneFolders.Remove(folder);

        auditLog.Log(folder.GuildId, userId, AuditActionType.SceneFolderDeleted, folder.Id, new { folder.Name });

        await BroadcastAsync(hub, hydrate, ctx, folder.GuildId, folder, removed: true);

        return Results.NoContent();
    }

    [WolverinePatch("/api/v1/guilds/{guildId}/scene-folders/reorder")]
    public async Task<IResult> ReorderFoldersAsync(string guildId, ReorderSceneFoldersDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var folders = await ctx.SceneFolders.Where(f => f.GuildId == guildId).ToListAsync();

        // Reject partial lists: position comes from the array index, so a subset would silently
        // reposition the folders it left out.
        var requested = dto.FolderIds.Distinct().ToList();
        if (requested.Count != folders.Count || folders.Any(f => !requested.Contains(f.Id)))
            return Results.BadRequest("FolderIds must contain every folder in this guild exactly once.");

        for (var i = 0; i < requested.Count; i++)
            folders.First(f => f.Id == requested[i]).Position = i;

        auditLog.Log(guildId, userId, AuditActionType.SceneFoldersReordered, guildId);

        await BroadcastTaxonomyAsync(hub, hydrate, ctx, guildId);

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════ Tags
    // ══════════════════════════════════════════════════════════════════════

    [WolverinePost("/api/v1/guilds/{guildId}/scene-tags")]
    public async Task<IResult> CreateTagAsync(string guildId, CreateSceneTagDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await Gate(permissionService, ctx, guildId, userId) is { } denied) return denied;

        var existing = await ctx.SceneTags.Where(t => t.GuildId == guildId).ToListAsync();

        if (existing.Count >= SceneTag.MaxTagsPerGuild)
            return Fault("scene_tag_limit", $"A guild can have at most {SceneTag.MaxTagsPerGuild} scene tags.");

        var name = dto.Name?.Trim() ?? "";
        if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Fault("scene_tag_name_taken", $"A tag named '{name}' already exists in this guild.",
                StatusCodes.Status409Conflict);

        SceneTag tag;
        try
        {
            tag = SceneTag.Create(new CreateSceneTagParams
            {
                GuildId = guildId,
                Name = name,
                Color = dto.Color,
                EmojiId = dto.EmojiId,
                EmojiName = dto.EmojiName,
                Position = existing.Count == 0 ? 0 : existing.Max(t => t.Position) + 1,
                Moderated = dto.Moderated,
            });
        }
        catch (ValidationException validationException)
        {
            return ValidationProblem(validationException);
        }

        ctx.SceneTags.Add(tag);

        auditLog.Log(guildId, userId, AuditActionType.SceneTagCreated, tag.Id, new { tag.Name });

        await BroadcastTaxonomyAsync(hub, hydrate, ctx, guildId, extraTag: tag);

        return Results.Ok(SceneTagDto.From(tag));
    }

    [WolverinePatch("/api/v1/scene-tags/{tagId}")]
    public async Task<IResult> UpdateTagAsync(string tagId, UpdateSceneTagDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var tag = await ctx.SceneTags.FirstOrDefaultAsync(t => t.Id == tagId);
        if (tag is null) return Results.NotFound();

        if (await Gate(permissionService, ctx, tag.GuildId, userId) is { } denied) return denied;

        if (dto.Name is not null)
        {
            var name = dto.Name.Trim();
            var taken = await ctx.SceneTags.AnyAsync(t =>
                t.GuildId == tag.GuildId && t.Id != tag.Id && t.Name.ToLower() == name.ToLower());

            if (taken)
                return Fault("scene_tag_name_taken", $"A tag named '{name}' already exists in this guild.",
                    StatusCodes.Status409Conflict);
        }

        try
        {
            tag.Update(new SceneTag.UpdateSceneTagParams
            {
                Name = dto.Name,
                Color = dto.Color,
                EmojiId = dto.EmojiId,
                EmojiName = dto.EmojiName,
                Moderated = dto.Moderated,
            });
        }
        catch (ValidationException validationException)
        {
            return ValidationProblem(validationException);
        }

        tag.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(tag.GuildId, userId, AuditActionType.SceneTagUpdated, tag.Id, new { tag.Name });

        // A rename or recolour applies to every scene carrying the tag, so no per-scene event
        // follows: clients re-render from their taxonomy cache.
        await BroadcastTaxonomyAsync(hub, hydrate, ctx, tag.GuildId);

        return Results.Ok(SceneTagDto.From(tag));
    }

    [WolverineDelete("/api/v1/scene-tags/{tagId}")]
    public async Task<IResult> DeleteTagAsync(string tagId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var tag = await ctx.SceneTags.FirstOrDefaultAsync(t => t.Id == tagId);
        if (tag is null) return Results.NotFound();

        if (await Gate(permissionService, ctx, tag.GuildId, userId) is { } denied) return denied;

        // Explicit for the same reason ForumTagEndpoint does it: Postgres cascades, InMemory does
        // not, and the two must not disagree about the change tracker after this call.
        var applications = await ctx.SceneTagAssignments.Where(a => a.TagId == tagId).ToListAsync();
        ctx.SceneTagAssignments.RemoveRange(applications);
        ctx.SceneTags.Remove(tag);

        auditLog.Log(tag.GuildId, userId, AuditActionType.SceneTagDeleted, tag.Id, new { tag.Name });

        await BroadcastTaxonomyAsync(hub, hydrate, ctx, tag.GuildId, removedTagId: tagId);

        return Results.NoContent();
    }

    /// <summary>
    /// Replaces a scene's tags. Any member who can see the scene may tag it; a moderated tag is the
    /// exception, and it is checked against the delta so an unrelated edit does not trip on one
    /// that was already there.
    /// </summary>
    [WolverinePut("/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/tags")]
    public async Task<IResult> SetSceneTagsAsync(string guildId, string sceneChannelId, SetSceneTagsDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService hydrate, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (await PersonaGate.CheckMembershipAsync(
                permissionService, ctx, guildId, userId, GuildFeatures.Scenes) is { } denied)
        {
            return denied;
        }

        var scene = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(
            c => c.Id == sceneChannelId && c.GuildId == guildId && c.Type == ChannelType.Scene);
        if (scene is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, sceneChannelId, Permissions.ViewChannel))
            return Results.Forbid();

        var wanted = dto.TagIds.Distinct(StringComparer.Ordinal).ToList();

        if (wanted.Count > SceneTag.MaxTagsPerScene)
            return Fault("scene_tag_limit", $"A scene can carry at most {SceneTag.MaxTagsPerScene} tags.");

        var known = await ctx.SceneTags.AsNoTracking().Where(t => t.GuildId == guildId).ToListAsync();
        var unknown = wanted.Where(id => known.All(t => t.Id != id)).ToList();
        if (unknown.Count > 0)
            return Fault("scene_tag_not_found", "That tag does not exist in this guild.");

        var current = await ctx.SceneTagAssignments
            .Where(a => a.SceneChannelId == sceneChannelId)
            .ToListAsync();

        var currentIds = current.Select(a => a.TagId).ToHashSet(StringComparer.Ordinal);
        var delta = wanted.Where(id => !currentIds.Contains(id))
            .Concat(currentIds.Where(id => !wanted.Contains(id)))
            .ToHashSet(StringComparer.Ordinal);

        if (delta.Count > 0 && known.Any(t => delta.Contains(t.Id) && t.Moderated)
            && !await permissionService.CanUserPerformActionOnGuildAsync(
                userId, guildId, ModulePermissions.ManageScenes))
        {
            return Fault("scene_tag_moderated", "That tag can only be applied by somebody who manages scenes.",
                StatusCodes.Status403Forbidden);
        }

        ctx.SceneTagAssignments.RemoveRange(current.Where(a => !wanted.Contains(a.TagId)));

        var now = DateTimeOffset.UtcNow;
        foreach (var tagId in wanted.Where(id => !currentIds.Contains(id)))
        {
            ctx.SceneTagAssignments.Add(new SceneTagAssignment
            {
                SceneChannelId = sceneChannelId, TagId = tagId, CreatedAt = now,
            });
        }

        auditLog.Log(guildId, userId, AuditActionType.SceneTagsApplied, sceneChannelId, new { TagIds = wanted });

        var presence = await hydrate.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId))
            .SendAsync("guild.SceneTagsChanged",
                new { GuildId = guildId, ChannelId = sceneChannelId, TagIds = wanted });

        return Results.Ok(new { tagIds = wanted });
    }

    // ══════════════════════════════════════════════════════════════════════ Helpers
    // ══════════════════════════════════════════════════════════════════════

    private static Task<IResult?> Gate(
        GuildPermissionService permissions, MicroserviceContext ctx, string guildId, string userId) =>
        PersonaGate.CheckAsync(permissions, ctx, guildId, userId,
            ModulePermissions.ManageScenes, GuildFeatures.Scenes);

    private static IResult Fault(string error, string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error, message }, statusCode: status);

    private static IResult ValidationProblem(ValidationException exception) =>
        Results.ValidationProblem(exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray()));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task<SceneTaxonomyDto> ReadTaxonomyAsync(MicroserviceContext ctx, string guildId)
    {
        var folders = await ctx.SceneFolders.AsNoTracking()
            .Where(f => f.GuildId == guildId)
            .OrderBy(f => f.Position).ThenBy(f => f.Name)
            .ToListAsync();

        var tags = await ctx.SceneTags.AsNoTracking()
            .Where(t => t.GuildId == guildId)
            .OrderBy(t => t.Position).ThenBy(t => t.Name)
            .ToListAsync();

        return new SceneTaxonomyDto
        {
            GuildId = guildId,
            Folders = folders.Select(SceneFolderDto.From).ToList(),
            Tags = tags.Select(SceneTagDto.From).ToList(),
        };
    }

    /// <summary>
    /// Sends the guild its whole vocabulary. Nothing is saved yet when this runs (Wolverine commits
    /// after the handler), so the pending change is folded into the read rather than queried back.
    /// </summary>
    private static async Task BroadcastTaxonomyAsync(
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate, MicroserviceContext ctx,
        string guildId, SceneFolder? extraFolder = null, bool folderRemoved = false,
        SceneTag? extraTag = null, string? removedTagId = null)
    {
        var payload = await ReadTaxonomyAsync(ctx, guildId);

        if (extraFolder is not null)
        {
            payload.Folders.RemoveAll(f => f.Id == extraFolder.Id);
            if (!folderRemoved) payload.Folders.Add(SceneFolderDto.From(extraFolder));
        }

        if (folderRemoved && extraFolder is not null)
        {
            foreach (var child in payload.Folders.Where(f => f.ParentFolderId == extraFolder.Id))
                child.ParentFolderId = null;
        }

        if (extraTag is not null && payload.Tags.All(t => t.Id != extraTag.Id))
            payload.Tags.Add(SceneTagDto.From(extraTag));

        if (removedTagId is not null) payload.Tags.RemoveAll(t => t.Id == removedTagId);

        var presence = await hydrate.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync(TaxonomyEvent, payload);
    }

    private static Task BroadcastAsync(
        IHubContext<EchoRealtimeHub> hub, GuildHydrateService hydrate, MicroserviceContext ctx,
        string guildId, SceneFolder folder, bool removed = false) =>
        BroadcastTaxonomyAsync(hub, hydrate, ctx, guildId, extraFolder: folder, folderRemoved: removed);
}
