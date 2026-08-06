using System.Security.Claims;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Wiki;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class WikiEndpoint
{
    /// <summary>Cover urls point at already-uploaded storage; the cap only exists so the column
    /// cannot be used as a text field.</summary>
    private const int MaxCoverUrlLength = 2048;

    /// <summary>A comment is a paragraph, not a page. Anything longer belongs in the wiki.</summary>
    private const int MaxCommentLength = 4000;

    /// <param name="includeContent">Returns each page's body alongside its summary.</param>
    [WolverineGet("/api/v1/guilds/{guildId}/wiki")]
    public async Task<IResult> GetWiki(
        string guildId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        bool includeContent = false)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var wiki = await ctx.Wikis.FirstOrDefaultAsync(w => w.GuildId == guildId);
        if (wiki is null)
        {
            wiki = Wiki.Create(guildId);
            ctx.Wikis.Add(wiki);
            await ctx.SaveChangesAsync();
        }

        var pages = await ctx.WikiPages
            .Where(p => p.GuildId == guildId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        // A grouped count, not Include(p => p.Revisions). The Include materialised every
        // revision of every page - each carrying a full copy of the page body at that point in
        // time - purely to read Count on the loaded collection. A wiki with 50 pages and 10
        // revisions each pulled 500 page-sized rows out of the database to produce 50 integers,
        // on every single wiki load. That dwarfed the page content this endpoint actually
        // returns, and it grew with edit history rather than with wiki size.
        var pageIds = pages.Select(p => p.Id).ToList();
        var revisionCounts = (await ctx.WikiRevisions
                .Where(r => pageIds.Contains(r.PageId))
                .GroupBy(r => r.PageId)
                .Select(g => new { PageId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.PageId, x => x.Count);

        // Same grouped-count shape as the revisions above, for the same reason: the tree wants a
        // badge number per page, not the rows behind it.
        var reactionCounts = (await ctx.WikiPageReactions
                .Where(r => pageIds.Contains(r.PageId))
                .GroupBy(r => r.PageId)
                .Select(g => new { PageId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.PageId, x => x.Count);

        var commentCounts = (await ctx.WikiComments
                .Where(c => pageIds.Contains(c.PageId))
                .GroupBy(c => c.PageId)
                .Select(g => new { PageId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.PageId, x => x.Count);

        // Only this caller's watches - one query for the whole wiki instead of one per page.
        var watchedPageIds = (await ctx.WikiPageWatchers
                .Where(w => w.UserId == userId && w.GuildId == guildId)
                .Select(w => w.PageId)
                .ToListAsync())
            .ToHashSet();

        var categories = await ctx.WikiCategories
            .Where(c => c.GuildId == guildId)
            .OrderBy(c => c.Position)
            .ToListAsync();

        return Results.Ok(new WikiDto
        {
            Id = wiki.Id,
            GuildId = wiki.GuildId,
            Categories = categories.Select(c => c.ToFacet<WikiCategory, WikiCategoryDto>()).ToList(),
            Pages = pages.Select(p =>
            {
                var summary = p.ToFacet<WikiPage, WikiPageSummaryDto>();
                // A page with no revisions has no group and so no row at all.
                summary.RevisionCount = revisionCounts.GetValueOrDefault(p.Id);
                summary.ReactionCount = reactionCounts.GetValueOrDefault(p.Id);
                summary.CommentCount = commentCounts.GetValueOrDefault(p.Id);
                summary.IsWatching = watchedPageIds.Contains(p.Id);
                if (includeContent) summary.Content = p.Content;
                return summary;
            }).ToList(),
        });
    }

    [WolverineGet("/api/v1/guilds/{guildId}/wiki/pages/{pageId}")]
    public async Task<IResult> GetWikiPage(
        string guildId,
        string pageId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var page = await ctx.WikiPages
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);

        if (page is null) return Results.NotFound();

        var dto = page.ToFacet<WikiPage, WikiPageDto>();
        dto.RevisionCount = await ctx.WikiRevisions
            .CountAsync(r => r.PageId == pageId);
        await HydrateEngagementAsync(dto, pageId, userId, ctx);

        return Results.Ok(dto);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/wiki/pages")]
    public async Task<IResult> CreateWikiPage(
        string guildId,
        CreateWikiPageDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canCreate = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.CreateWikiPages);
        if (!canCreate) return Results.Forbid();

        if (!string.IsNullOrEmpty(dto.Icon) && !EmojiText.IsSingleEmoji(dto.Icon))
            return Results.BadRequest("Page icon must be a single emoji.");
        if (dto.CoverUrl is { Length: > MaxCoverUrlLength })
            return Results.BadRequest($"Cover url must be at most {MaxCoverUrlLength} characters.");

        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = guildId,
            Title = dto.Title,
            Content = dto.Content ?? string.Empty,
            AuthorId = userId,
            ParentPageId = dto.ParentPageId,
            CategoryId = dto.CategoryId,
            Visibility = dto.Visibility ?? WikiVisibility.Public,
            Tags = dto.Tags ?? [],
            IsPinned = dto.IsPinned ?? false,
            Icon = string.IsNullOrEmpty(dto.Icon) ? null : dto.Icon,
            CoverUrl = string.IsNullOrWhiteSpace(dto.CoverUrl) ? null : dto.CoverUrl.Trim(),
        });

        ctx.WikiPages.Add(page);

        var responseDto = page.ToFacet<WikiPage, WikiPageDto>();
        responseDto.RevisionCount = page.Revisions.Count;
        return Results.Ok(responseDto);
    }

    [WolverinePut("/api/v1/guilds/{guildId}/wiki/pages/{pageId}")]
    public async Task<IResult> UpdateWikiPage(
        string guildId,
        string pageId,
        UpdateWikiPageDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var page = await ctx.WikiPages
            .Include(p => p.Revisions)
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);

        if (page is null) return Results.NotFound();

        // Where a page sits is the wiki's shape, not the page's content - the same thing
        // ManageWikiStructure already governs for categories.
        var movesPage = dto.ParentPageId.HasValue || dto.CategoryId.HasValue;
        var changesContent = dto.Title is not null || dto.Content is not null || dto.Visibility is not null
                             || dto.Tags is not null || dto.IsPinned is not null
                             || dto.Icon.HasValue || dto.CoverUrl.HasValue;

        var isOwn = page.AuthorId == userId;
        var requiredPermission = isOwn ? Permissions.EditOwnWikiPages : Permissions.EditAnyWikiPage;
        var canEdit = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
        if (!canEdit)
        {
            if (changesContent || !movesPage) return Results.Forbid();

            var canManageStructure = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageWikiStructure);
            if (!canManageStructure) return Results.Forbid();
        }

        if (!string.IsNullOrEmpty(dto.Icon.Value) && !EmojiText.IsSingleEmoji(dto.Icon.Value))
            return Results.BadRequest("Page icon must be a single emoji.");
        if (dto.CoverUrl.Value is { Length: > MaxCoverUrlLength })
            return Results.BadRequest($"Cover url must be at most {MaxCoverUrlLength} characters.");

        var contentChanged = dto.Content is not null && dto.Content != page.Content;

        if (dto.Title is not null) page.Title = dto.Title;
        if (dto.Content is not null) page.Content = dto.Content;
        // HasValue, not "is not null": an omitted property leaves the page alone, an explicit null
        // clears it.
        if (dto.ParentPageId.HasValue) page.ParentPageId = dto.ParentPageId.Value;
        if (dto.CategoryId.HasValue) page.CategoryId = dto.CategoryId.Value;
        if (dto.Visibility is not null) page.Visibility = dto.Visibility.Value;
        if (dto.Tags is not null) page.Tags = dto.Tags;
        if (dto.IsPinned is not null) page.IsPinned = dto.IsPinned.Value;
        // Empty string clears as well as null, so a client that binds an icon picker to a text
        // input does not have to translate "" into a JSON null to mean the same thing.
        if (dto.Icon.HasValue) page.Icon = string.IsNullOrEmpty(dto.Icon.Value) ? null : dto.Icon.Value;
        if (dto.CoverUrl.HasValue) page.CoverUrl = dto.CoverUrl.Value?.Trim() is { Length: > 0 } url ? url : null;
        page.LastEditorId = userId;

        if (contentChanged)
        {
            var nextRevisionNumber = page.Revisions.Count > 0
                ? page.Revisions.Max(r => r.RevisionNumber) + 1
                : 1;

            var revision = WikiRevision.Create(new CreateWikiRevisionParams
            {
                PageId = page.Id,
                Content = page.Content,
                EditorId = userId,
                RevisionNumber = nextRevisionNumber,
                // Only meaningful here: a summary describes a content change, and this is the
                // only branch where one exists to describe.
                Summary = string.IsNullOrWhiteSpace(dto.Summary) ? null : dto.Summary.Trim(),
            });
            // Not also page.Revisions.Add(revision) - EF Core's change-tracker fixup already
            // appends it to page.Revisions automatically once revision.PageId matches this
            // already-tracked page (adding it explicitly too duplicated the in-memory list entry,
            // inflating WikiPageDto.RevisionCount by one in the response below).
            ctx.WikiRevisions.Add(revision);
        }

        page.RaiseUpdated(userId);

        var responseDto = page.ToFacet<WikiPage, WikiPageDto>();
        responseDto.RevisionCount = page.Revisions.Count;
        await HydrateEngagementAsync(responseDto, page.Id, userId, ctx);
        return Results.Ok(responseDto);
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/wiki/pages/{pageId}")]
    public async Task<IResult> DeleteWikiPage(
        string guildId,
        string pageId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var page = await ctx.WikiPages.FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (page is null) return Results.NotFound();

        var canDelete = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.DeleteWikiPages);
        if (!canDelete) return Results.Forbid();

        page.RaiseDeleted();
        ctx.WikiPages.Remove(page);

        return Results.NoContent();
    }

    [WolverineGet("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/revisions")]
    public async Task<IResult> GetWikiPageRevisions(
        string guildId,
        string pageId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return Results.NotFound();

        var revisions = await ctx.WikiRevisions
            .Where(r => r.PageId == pageId)
            .OrderByDescending(r => r.RevisionNumber)
            .ToFacetsAsync<WikiRevision, WikiRevisionDto>();

        return Results.Ok(revisions);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/revisions/{revisionId}/restore")]
    public async Task<IResult> RestoreWikiRevision(
        string guildId,
        string pageId,
        string revisionId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageWikiRevisions);
        if (!canManage) return Results.Forbid();

        var page = await ctx.WikiPages
            .Include(p => p.Revisions)
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);

        if (page is null) return Results.NotFound();

        var revision = page.Revisions.FirstOrDefault(r => r.Id == revisionId);
        if (revision is null) return Results.NotFound();

        page.Content = revision.Content;
        page.LastEditorId = userId;

        var nextRevisionNumber = page.Revisions.Max(r => r.RevisionNumber) + 1;
        var restoredRevision = WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = page.Id,
            Content = revision.Content,
            EditorId = userId,
            RevisionNumber = nextRevisionNumber,
            Summary = $"Restored from revision #{revision.RevisionNumber}",
        });
        // Not also page.Revisions.Add(restoredRevision) - see the identical note in UpdateWikiPage
        // above; EF's change-tracker fixup already appends it once tracked via the DbSet.
        ctx.WikiRevisions.Add(restoredRevision);

        page.RaiseUpdated(userId);

        var responseDto = page.ToFacet<WikiPage, WikiPageDto>();
        responseDto.RevisionCount = page.Revisions.Count;
        await HydrateEngagementAsync(responseDto, page.Id, userId, ctx);
        return Results.Ok(responseDto);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/wiki/categories")]
    public async Task<IResult> CreateWikiCategory(
        string guildId,
        CreateWikiCategoryDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageWikiStructure);
        if (!canManage) return Results.Forbid();

        var position = dto.Position ?? await ctx.WikiCategories
            .Where(c => c.GuildId == guildId)
            .CountAsync();

        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = guildId,
            Name = dto.Name,
            Position = position,
            ParentCategoryId = dto.ParentCategoryId,
        });

        ctx.WikiCategories.Add(category);

        return Results.Ok(category.ToFacet<WikiCategory, WikiCategoryDto>());
    }

    [WolverinePut("/api/v1/guilds/{guildId}/wiki/categories/{categoryId}")]
    public async Task<IResult> UpdateWikiCategory(
        string guildId,
        string categoryId,
        UpdateWikiCategoryDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var category = await ctx.WikiCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.GuildId == guildId);
        if (category is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageWikiStructure);
        if (!canManage) return Results.Forbid();

        if (dto.Name is not null) category.Name = dto.Name;
        if (dto.Position is not null) category.Position = dto.Position.Value;
        category.ParentCategoryId = dto.ParentCategoryId;

        category.RaiseUpdated();

        return Results.Ok(category.ToFacet<WikiCategory, WikiCategoryDto>());
    }

    [WolverineDelete("/api/v1/guilds/{guildId}/wiki/categories/{categoryId}")]
    public async Task<IResult> DeleteWikiCategory(
        string guildId,
        string categoryId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var category = await ctx.WikiCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.GuildId == guildId);
        if (category is null) return Results.NotFound();

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageWikiStructure);
        if (!canManage) return Results.Forbid();

        category.RaiseDeleted();
        ctx.WikiCategories.Remove(category);

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Reactions

    /// <summary>
    /// Idempotent: reacting twice with the same emoji is the same row, and the second call emits no
    /// event.
    /// </summary>
    [WolverinePost("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/reactions")]
    public async Task<(IResult, WikiPageReactionAdded?)> AddWikiPageReaction(
        string guildId,
        string pageId,
        CreateWikiPageReactionDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var canReact = await permissionService.CanUserPerformActionOnGuildAsync(
            userId, guildId, Permissions.ViewWiki | Permissions.AddReactions);
        if (!canReact) return (Results.Forbid(), null);

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return (Results.NotFound(), null);

        // The whole page's reactions, because the response is the fresh aggregate either way and a
        // page carries a handful of rows.
        var rows = await ctx.WikiPageReactions.Where(r => r.PageId == pageId).ToListAsync();

        if (rows.Any(r => r.UserId == userId && r.Emoji == dto.Emoji))
            return (Results.Ok(AggregateReactions(rows, userId)), null);

        WikiPageReaction reaction;
        try
        {
            reaction = WikiPageReaction.Create(new CreateWikiPageReactionParams
            {
                PageId = pageId, GuildId = guildId, UserId = userId, Emoji = dto.Emoji,
            });
        }
        catch (ArgumentException ex)
        {
            return (Results.BadRequest(ex.Message), null);
        }

        ctx.WikiPageReactions.Add(reaction);
        rows.Add(reaction);

        return (Results.Ok(AggregateReactions(rows, userId)), new WikiPageReactionAdded
        {
            CorrelationId = pageId,
            PageId = pageId,
            GuildId = guildId,
            UserId = userId,
            Emoji = dto.Emoji,
        });
    }

    /// <summary>
    /// Removes only the caller's own reaction - there is no "clear everyone's 👍" here, matching
    /// message reactions.
    /// </summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/reactions/{emoji}")]
    public async Task<(IResult, WikiPageReactionRemoved?)> RemoveWikiPageReaction(
        string guildId,
        string pageId,
        string emoji,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        // Only ViewWiki: taking your own reaction back is not an act of reacting, so someone whose
        // AddReactions was revoked after the fact can still undo what they did.
        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return (Results.Forbid(), null);

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return (Results.NotFound(), null);

        var rows = await ctx.WikiPageReactions.Where(r => r.PageId == pageId).ToListAsync();
        var mine = rows.FirstOrDefault(r => r.UserId == userId && r.Emoji == emoji);
        if (mine is null) return (Results.Ok(AggregateReactions(rows, userId)), null);

        ctx.WikiPageReactions.Remove(mine);
        rows.Remove(mine);

        return (Results.Ok(AggregateReactions(rows, userId)), new WikiPageReactionRemoved
        {
            CorrelationId = pageId,
            PageId = pageId,
            GuildId = guildId,
            UserId = userId,
            Emoji = emoji,
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Watchers

    /// <summary>Idempotent - watching an already-watched page is a no-op returning 200.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/watch")]
    public async Task<IResult> WatchWikiPage(
        string guildId,
        string pageId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return Results.NotFound();

        var already = await ctx.WikiPageWatchers.AnyAsync(w => w.PageId == pageId && w.UserId == userId);
        if (!already)
        {
            ctx.WikiPageWatchers.Add(WikiPageWatcher.Create(new CreateWikiPageWatcherParams
            {
                PageId = pageId, GuildId = guildId, UserId = userId,
            }));
        }

        var watcherCount = await ctx.WikiPageWatchers.CountAsync(w => w.PageId == pageId) + (already ? 0 : 1);
        return Results.Ok(new WikiWatchStateDto { PageId = pageId, IsWatching = true, WatcherCount = watcherCount });
    }

    /// <summary>Idempotent - unwatching a page you do not watch is a no-op returning 200.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/watch")]
    public async Task<IResult> UnwatchWikiPage(
        string guildId,
        string pageId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return Results.NotFound();

        var watch = await ctx.WikiPageWatchers.FirstOrDefaultAsync(w => w.PageId == pageId && w.UserId == userId);
        if (watch is not null) ctx.WikiPageWatchers.Remove(watch);

        var watcherCount = await ctx.WikiPageWatchers.CountAsync(w => w.PageId == pageId) - (watch is null ? 0 : 1);
        return Results.Ok(new WikiWatchStateDto { PageId = pageId, IsWatching = false, WatcherCount = watcherCount });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Comments

    [WolverineGet("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/comments")]
    public async Task<IResult> GetWikiPageComments(
        string guildId,
        string pageId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return Results.NotFound();

        var canModerate = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ModerateWikiComments);

        var comments = await ctx.WikiComments
            .Where(c => c.PageId == pageId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        return Results.Ok(comments.Select(c => ToCommentDto(c, userId, canModerate)).ToList());
    }

    [WolverinePost("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/comments")]
    public async Task<IResult> CreateWikiComment(
        string guildId,
        string pageId,
        CreateWikiCommentDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var pageExists = await ctx.WikiPages.AnyAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (!pageExists) return Results.NotFound();

        if (dto.Content is { Length: > MaxCommentLength })
            return Results.BadRequest($"Comment must be at most {MaxCommentLength} characters.");

        WikiComment comment;
        try
        {
            comment = WikiComment.Create(new CreateWikiCommentParams
            {
                PageId = pageId, GuildId = guildId, AuthorId = userId, Content = dto.Content,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        ctx.WikiComments.Add(comment);

        return Results.Ok(ToCommentDto(comment, userId, canModerate: false));
    }

    /// <summary>
    /// Author only, deliberately: ModerateWikiComments lets a moderator remove a comment, not
    /// rewrite what somebody said under their own name.
    /// </summary>
    [WolverinePut("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/comments/{commentId}")]
    public async Task<IResult> UpdateWikiComment(
        string guildId,
        string pageId,
        string commentId,
        UpdateWikiCommentDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var comment = await ctx.WikiComments
            .FirstOrDefaultAsync(c => c.Id == commentId && c.PageId == pageId && c.GuildId == guildId);
        if (comment is null) return Results.NotFound();

        if (comment.AuthorId != userId) return Results.Forbid();

        if (dto.Content is { Length: > MaxCommentLength })
            return Results.BadRequest($"Comment must be at most {MaxCommentLength} characters.");

        try
        {
            comment.Edit(dto.Content);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        comment.RaiseUpdated();

        return Results.Ok(ToCommentDto(comment, userId, canModerate: false));
    }

    /// <summary>Own comment, or anyone holding ModerateWikiComments.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/comments/{commentId}")]
    public async Task<IResult> DeleteWikiComment(
        string guildId,
        string pageId,
        string commentId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var comment = await ctx.WikiComments
            .FirstOrDefaultAsync(c => c.Id == commentId && c.PageId == pageId && c.GuildId == guildId);
        if (comment is null) return Results.NotFound();

        var isOwn = comment.AuthorId == userId;
        var requiredPermission = isOwn ? Permissions.ViewWiki : Permissions.ModerateWikiComments;
        var canDelete = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
        if (!canDelete) return Results.Forbid();

        comment.RaiseDeleted();
        ctx.WikiComments.Remove(comment);

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Fills the engagement fields of a page DTO.</summary>
    private static async Task HydrateEngagementAsync(WikiPageDto dto, string pageId, string userId, MicroserviceContext ctx)
    {
        var reactions = await ctx.WikiPageReactions.Where(r => r.PageId == pageId).ToListAsync();
        dto.Reactions = AggregateReactions(reactions, userId);
        dto.WatcherCount = await ctx.WikiPageWatchers.CountAsync(w => w.PageId == pageId);
        dto.IsWatching = await ctx.WikiPageWatchers.AnyAsync(w => w.PageId == pageId && w.UserId == userId);
        dto.CommentCount = await ctx.WikiComments.CountAsync(c => c.PageId == pageId);
    }

    /// <summary>Busiest emoji first, ties broken by codepoint so the order is stable across
    /// requests rather than whatever the database happened to return.</summary>
    private static List<WikiReactionDto> AggregateReactions(IEnumerable<WikiPageReaction> rows, string userId) =>
        rows.GroupBy(r => r.Emoji)
            .Select(g => new WikiReactionDto
            {
                Emoji = g.Key,
                Count = g.Count(),
                Me = g.Any(r => r.UserId == userId),
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Emoji, StringComparer.Ordinal)
            .ToList();

    private static WikiCommentDto ToCommentDto(WikiComment comment, string userId, bool canModerate)
    {
        var dto = comment.ToFacet<WikiComment, WikiCommentDto>();
        dto.CanDelete = comment.AuthorId == userId || canModerate;
        return dto;
    }
}
