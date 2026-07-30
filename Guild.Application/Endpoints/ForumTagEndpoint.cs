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

/// <summary>Tag definitions and per-forum settings. Applying tags to a post lives in
/// ForumPostEndpoint instead - that's a post-author action gated on thread permissions, while
/// everything here is a moderator action gated on ManageChannel.
///
/// ManageChannel is resolved per *channel* rather than per guild (the guild-level check
/// ChannelEndpoint uses), so a channel-level permission overwrite granting someone control of one
/// forum doesn't hand them every forum in the guild.</summary>
[Authorize]
public class ForumTagEndpoint
{
    private static ForumTagDto ToDto(ForumTag tag, int postCount = 0) => new()
    {
        Id = tag.Id,
        ChannelId = tag.ChannelId,
        GuildId = tag.GuildId,
        Name = tag.Name,
        EmojiId = tag.EmojiId,
        EmojiName = tag.EmojiName,
        Color = tag.Color,
        Position = tag.Position,
        Moderated = tag.Moderated,
        PostCount = postCount,
    };

    private static IResult ValidationProblem(ValidationException exception) =>
        Results.ValidationProblem(exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(e => e.ErrorMessage).ToArray()));

    // ══════════════════════════════════════════════════════════════════════
    // Tags
    // ══════════════════════════════════════════════════════════════════════

    [WolverineGet("/api/v1/channels/{forumId}/tags")]
    public async Task<IResult> ListTagsAsync(string forumId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == forumId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, forumId, Permissions.ViewChannel))
            return Results.Forbid();

        var tags = await ctx.ForumTags.AsNoTracking()
            .Where(t => t.ChannelId == forumId)
            .OrderBy(t => t.Position).ThenBy(t => t.Name)
            .ToListAsync();

        var counts = await forumService.GetPostCountsAsync(tags.Select(t => t.Id).ToList());

        return Results.Ok(tags.Select(t => ToDto(t, counts.GetValueOrDefault(t.Id))).ToList());
    }

    [WolverinePost("/api/v1/channels/{forumId}/tags")]
    public async Task<IResult> CreateTagAsync(string forumId, CreateForumTagDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == forumId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, forumId, Permissions.ManageChannel))
            return Results.Forbid();

        var existing = await ctx.ForumTags.Where(t => t.ChannelId == forumId).ToListAsync();

        if (existing.Count >= ForumTag.MaxTagsPerForum)
            return Results.BadRequest($"A forum can have at most {ForumTag.MaxTagsPerForum} tags.");

        var name = dto.Name?.Trim() ?? "";
        if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict($"A tag named '{name}' already exists in this forum.");

        ForumTag tag;
        try
        {
            tag = ForumTag.Create(new CreateForumTagParams
            {
                ChannelId = forumId,
                GuildId = forum.GuildId,
                Name = name,
                EmojiId = dto.EmojiId,
                EmojiName = dto.EmojiName,
                Color = dto.Color,
                // Appended to the end; ordering is the reorder endpoint's job.
                Position = existing.Count == 0 ? 0 : existing.Max(t => t.Position) + 1,
                Moderated = dto.Moderated,
            });
        }
        catch (ValidationException validationException)
        {
            return ValidationProblem(validationException);
        }

        ctx.ForumTags.Add(tag);

        auditLog.Log(forum.GuildId, userId, AuditActionType.ForumTagCreated, tag.Id, new { tag.Name, ChannelId = forumId });

        await BroadcastAsync(hub, guildHydrateService, forum.GuildId, "guild.ForumTagCreated",
            new { GuildId = forum.GuildId, ChannelId = forumId, Tag = ToDto(tag) });

        return Results.Ok(ToDto(tag));
    }

    [WolverinePatch("/api/v1/forum-tags/{tagId}")]
    public async Task<IResult> UpdateTagAsync(string tagId, UpdateForumTagDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] AuditLogService auditLog,
        [NotBody] IHubContext<EchoRealtimeHub> hub, [NotBody] GuildHydrateService guildHydrateService,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var tag = await ctx.ForumTags.FirstOrDefaultAsync(t => t.Id == tagId);
        if (tag is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, tag.ChannelId, Permissions.ManageChannel))
            return Results.Forbid();

        if (dto.Name is not null)
        {
            var name = dto.Name.Trim();
            var taken = await ctx.ForumTags
                .AnyAsync(t => t.ChannelId == tag.ChannelId && t.Id != tag.Id && t.Name.ToLower() == name.ToLower());
            if (taken) return Results.Conflict($"A tag named '{name}' already exists in this forum.");
        }

        try
        {
            tag.Update(new ForumTag.UpdateForumTagParams
            {
                Name = dto.Name,
                EmojiId = dto.EmojiId,
                EmojiName = dto.EmojiName,
                Color = dto.Color,
                Moderated = dto.Moderated,
            });
        }
        catch (ValidationException validationException)
        {
            return ValidationProblem(validationException);
        }

        auditLog.Log(tag.GuildId, userId, AuditActionType.ForumTagUpdated, tag.Id, new { tag.Name });

        var counts = await forumService.GetPostCountsAsync([tag.Id]);
        var dtoOut = ToDto(tag, counts.GetValueOrDefault(tag.Id));

        // Renames and recolours apply retroactively to every post carrying the tag, so no
        // per-post event follows this one - clients re-render from their tag cache.
        await BroadcastAsync(hub, guildHydrateService, tag.GuildId, "guild.ForumTagUpdated",
            new { GuildId = tag.GuildId, ChannelId = tag.ChannelId, Tag = dtoOut });

        return Results.Ok(dtoOut);
    }

    [WolverineDelete("/api/v1/forum-tags/{tagId}")]
    public async Task<IResult> DeleteTagAsync(string tagId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var tag = await ctx.ForumTags.FirstOrDefaultAsync(t => t.Id == tagId);
        if (tag is null) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, tag.ChannelId, Permissions.ManageChannel))
            return Results.Forbid();

        // Postgres cascades the applications, but the InMemory provider used by the unit tests
        // doesn't - and leaving it implicit would mean the two disagree about what's in the
        // change tracker after this call. Removing them explicitly keeps both honest.
        var applications = await ctx.ForumPostTags.Where(pt => pt.TagId == tagId).ToListAsync();
        ctx.ForumPostTags.RemoveRange(applications);
        ctx.ForumTags.Remove(tag);

        auditLog.Log(tag.GuildId, userId, AuditActionType.ForumTagDeleted, tag.Id, new { tag.Name });

        await BroadcastAsync(hub, guildHydrateService, tag.GuildId, "guild.ForumTagDeleted",
            new { GuildId = tag.GuildId, ChannelId = tag.ChannelId, TagId = tagId });

        return Results.NoContent();
    }

    [WolverinePatch("/api/v1/channels/{forumId}/tags/reorder")]
    public async Task<IResult> ReorderTagsAsync(string forumId, ReorderForumTagsDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == forumId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, forumId, Permissions.ManageChannel))
            return Results.Forbid();

        var tags = await ctx.ForumTags.Where(t => t.ChannelId == forumId).ToListAsync();

        // Reject partial lists: positions come from the array index, so a subset would silently
        // reposition the tags it left out.
        var requested = dto.TagIds.Distinct().ToList();
        if (requested.Count != tags.Count || tags.Any(t => !requested.Contains(t.Id)))
            return Results.BadRequest("TagIds must contain every tag in this forum exactly once.");

        for (var i = 0; i < requested.Count; i++)
            tags.First(t => t.Id == requested[i]).Position = i;

        auditLog.Log(forum.GuildId, userId, AuditActionType.ForumTagsReordered, forumId);

        await BroadcastAsync(hub, guildHydrateService, forum.GuildId, "guild.ForumTagsReordered",
            new { GuildId = forum.GuildId, ChannelId = forumId, TagIds = requested });

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Config
    // ══════════════════════════════════════════════════════════════════════

    [WolverineGet("/api/v1/channels/{forumId}/forum-config")]
    public async Task<IResult> GetConfigAsync(string forumId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ForumService forumService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == forumId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, forumId, Permissions.ViewChannel))
            return Results.Forbid();

        var config = await forumService.GetConfigAsync(forumId, forum.GuildId);
        return Results.Ok(ForumConfigDto.From(config));
    }

    [WolverinePatch("/api/v1/channels/{forumId}/forum-config")]
    public async Task<IResult> UpdateConfigAsync(string forumId, UpdateForumConfigDto dto,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IHubContext<EchoRealtimeHub> hub,
        [NotBody] GuildHydrateService guildHydrateService, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var forum = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == forumId);
        if (forum is null || !forum.Type.IsForum()) return Results.NotFound();

        if (!await permissionService.CanUserPerformActionAsync(userId, forumId, Permissions.ManageChannel))
            return Results.Forbid();

        if (dto.DefaultAutoArchiveMinutes is not null &&
            !ForumConfig.AllowedAutoArchiveMinutes.Contains(dto.DefaultAutoArchiveMinutes.Value))
            return Results.BadRequest(
                $"DefaultAutoArchiveMinutes must be one of: {string.Join(", ", ForumConfig.AllowedAutoArchiveMinutes)}.");

        if (dto.DefaultThreadSlowModeSeconds is < 0)
            return Results.BadRequest("DefaultThreadSlowModeSeconds cannot be negative.");

        // First write is what actually inserts the row - reads hand back an unpersisted default,
        // so an untouched forum costs no storage.
        var config = await ctx.ForumConfigs.FirstOrDefaultAsync(c => c.ChannelId == forumId);
        if (config is null)
        {
            config = ForumConfig.Default(forumId, forum.GuildId);
            ctx.ForumConfigs.Add(config);
        }

        if (dto.RequireTag is not null) config.RequireTag = dto.RequireTag.Value;
        if (dto.DefaultSortOrder is not null) config.DefaultSortOrder = dto.DefaultSortOrder.Value;
        if (dto.DefaultLayout is not null) config.DefaultLayout = dto.DefaultLayout.Value;
        if (dto.DefaultThreadSlowModeSeconds is not null) config.DefaultThreadSlowModeSeconds = dto.DefaultThreadSlowModeSeconds.Value;
        if (dto.DefaultAutoArchiveMinutes is not null) config.DefaultAutoArchiveMinutes = dto.DefaultAutoArchiveMinutes.Value;

        // Same one-emoji-at-a-time rule as ForumTag: setting either side clears the other.
        if (dto.DefaultReactionEmojiId is not null)
        {
            config.DefaultReactionEmojiId = string.IsNullOrWhiteSpace(dto.DefaultReactionEmojiId) ? null : dto.DefaultReactionEmojiId;
            if (config.DefaultReactionEmojiId is not null) config.DefaultReactionEmojiName = null;
        }

        if (dto.DefaultReactionEmojiName is not null)
        {
            config.DefaultReactionEmojiName = string.IsNullOrWhiteSpace(dto.DefaultReactionEmojiName) ? null : dto.DefaultReactionEmojiName;
            if (config.DefaultReactionEmojiName is not null) config.DefaultReactionEmojiId = null;
        }

        auditLog.Log(forum.GuildId, userId, AuditActionType.ForumConfigUpdated, forumId);

        var dtoOut = ForumConfigDto.From(config);

        await BroadcastAsync(hub, guildHydrateService, forum.GuildId, "guild.ForumConfigUpdated",
            new { GuildId = forum.GuildId, ChannelId = forumId, Config = dtoOut });

        return Results.Ok(dtoOut);
    }

    private static async Task BroadcastAsync(IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService guildHydrateService, string guildId, string eventName, object payload)
    {
        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync(eventName, payload);
    }
}
