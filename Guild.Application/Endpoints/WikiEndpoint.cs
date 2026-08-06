using System.Security.Claims;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
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
                             || dto.Tags is not null || dto.IsPinned is not null;

        var isOwn = page.AuthorId == userId;
        var requiredPermission = isOwn ? Permissions.EditOwnWikiPages : Permissions.EditAnyWikiPage;
        var canEdit = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
        if (!canEdit)
        {
            if (changesContent || !movesPage) return Results.Forbid();

            var canManageStructure = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageWikiStructure);
            if (!canManageStructure) return Results.Forbid();
        }

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

        page.RaiseUpdated();

        var responseDto = page.ToFacet<WikiPage, WikiPageDto>();
        responseDto.RevisionCount = page.Revisions.Count;
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

        page.RaiseUpdated();

        var responseDto = page.ToFacet<WikiPage, WikiPageDto>();
        responseDto.RevisionCount = page.Revisions.Count;
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

}
