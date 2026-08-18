using System.Security.Claims;
using AppEnvironment;
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

    /// <summary>The label the public wiki host is derived under, matching the gateway's.</summary>
    private const string WikiSiteLabel = "wiki";

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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var wiki = await ctx.Wikis.FirstOrDefaultAsync(w => w.GuildId == guildId);
        if (wiki is null)
        {
            wiki = Wiki.Create(guildId);
            ctx.Wikis.Add(wiki);
            await ctx.SaveChangesAsync();
        }

        // Visibility finally does something. It was written by two endpoints and read by nothing,
        // which is why it cannot be the column that grants public access - see WikiPublication.
        var canEditAny = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.EditAnyWikiPage);

        var pages = await ctx.WikiPages
            .Where(p => p.GuildId == guildId)
            .Where(p => canEditAny || p.Visibility == WikiVisibility.Public || p.AuthorId == userId)
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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var page = await ctx.WikiPages
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);

        if (page is null) return Results.NotFound();

        // 404 rather than 403: a private page nobody may read should not be confirmed to exist.
        if (!await CanSeeAsync(page, userId, guildId, permissionService)) return Results.NotFound();

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

        var canCreate = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.CreateWikiPages);
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
            InfoboxJson = string.IsNullOrWhiteSpace(dto.InfoboxJson) ? null : dto.InfoboxJson,
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
                             || dto.Icon.HasValue || dto.CoverUrl.HasValue || dto.InfoboxJson.HasValue;

        var isOwn = page.AuthorId == userId;
        var requiredPermission = isOwn ? ModulePermissions.EditOwnWikiPages : ModulePermissions.EditAnyWikiPage;
        var canEdit = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
        if (!canEdit)
        {
            if (changesContent || !movesPage) return Results.Forbid();

            var canManageStructure = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageWikiStructure);
            if (!canManageStructure) return Results.Forbid();
        }

        if (!string.IsNullOrEmpty(dto.Icon.Value) && !EmojiText.IsSingleEmoji(dto.Icon.Value))
            return Results.BadRequest("Page icon must be a single emoji.");
        if (dto.CoverUrl.Value is { Length: > MaxCoverUrlLength })
            return Results.BadRequest($"Cover url must be at most {MaxCoverUrlLength} characters.");

        // The infobox counts: an infobox-only edit used to version nothing and bump no revision
        // number, so "who quietly buffed their own stats" was not a diff anybody could review.
        var infoboxChanged = dto.InfoboxJson.HasValue && dto.InfoboxJson.Value != page.InfoboxJson;
        var contentChanged = (dto.Content is not null && dto.Content != page.Content) || infoboxChanged;

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
        if (dto.InfoboxJson.HasValue) page.InfoboxJson = string.IsNullOrWhiteSpace(dto.InfoboxJson.Value) ? null : dto.InfoboxJson.Value;
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
                InfoboxJson = page.InfoboxJson,
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

        var canDelete = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.DeleteWikiPages);
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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var page = await ctx.WikiPages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (page is null) return Results.NotFound();
        if (!await CanSeeAsync(page, userId, guildId, permissionService)) return Results.NotFound();

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

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageWikiRevisions);
        if (!canManage) return Results.Forbid();

        var page = await ctx.WikiPages
            .Include(p => p.Revisions)
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);

        if (page is null) return Results.NotFound();

        var revision = page.Revisions.FirstOrDefault(r => r.Id == revisionId);
        if (revision is null) return Results.NotFound();

        page.Content = revision.Content;
        page.InfoboxJson = revision.InfoboxJson;
        page.LastEditorId = userId;

        var nextRevisionNumber = page.Revisions.Max(r => r.RevisionNumber) + 1;
        var restoredRevision = WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = page.Id,
            Content = revision.Content,
            InfoboxJson = revision.InfoboxJson,
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

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageWikiStructure);
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
            InfoboxTemplateJson = string.IsNullOrWhiteSpace(dto.InfoboxTemplateJson) ? null : dto.InfoboxTemplateJson,
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

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageWikiStructure);
        if (!canManage) return Results.Forbid();

        if (dto.Name is not null) category.Name = dto.Name;
        if (dto.Position is not null) category.Position = dto.Position.Value;
        category.ParentCategoryId = dto.ParentCategoryId;
        if (dto.InfoboxTemplateJson.HasValue)
            category.InfoboxTemplateJson = string.IsNullOrWhiteSpace(dto.InfoboxTemplateJson.Value)
                ? null
                : dto.InfoboxTemplateJson.Value;

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

        var canManage = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ManageWikiStructure);
        if (!canManage) return Results.Forbid();

        category.RaiseDeleted();
        ctx.WikiCategories.Remove(category);

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Publishing

    /// <summary>Whether this wiki is on the public host, and whether the plan still covers it.</summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="permissionService">Resolves the caller's mask.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="user">The caller.</param>
    /// <param name="vanity">The entitlement gate publishing shares with vanity URLs.</param>
    /// <returns>The publication state, or 403 without PublishWikiPublicly.</returns>
    [WolverineGet("/api/v1/guilds/{guildId}/wiki/publication")]
    public async Task<IResult> GetWikiPublication(
        string guildId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        [NotBody] VanityUrlService vanity)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canPublish = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.PublishWikiPublicly);
        if (!canPublish) return Results.Forbid();

        var wiki = await ctx.Wikis.AsNoTracking().FirstOrDefaultAsync(w => w.GuildId == guildId);

        // Not strict: this is a settings screen, and the same reasoning as the vanity-URL read
        // applies - a Billing hiccup should not tell an owner their wiki is gone.
        var entitled = await vanity.IsEntitledAsync(guildId, strict: false);

        return Results.Ok(await DescribePublicationAsync(guildId, wiki, entitled, ctx));
    }

    /// <summary>
    /// Claims the slug this wiki is published on, or clears it. This is the master switch: while the
    /// slug is null no page of this guild is reachable anonymously, whatever any page says.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="dto">The slug to claim, or null to unpublish.</param>
    /// <param name="permissionService">Resolves the caller's mask.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="user">The caller.</param>
    /// <param name="vanity">The entitlement gate publishing shares with vanity URLs.</param>
    /// <param name="auditLog">Records who published what.</param>
    /// <returns>The new publication state.</returns>
    [WolverinePut("/api/v1/guilds/{guildId}/wiki/publication")]
    public async Task<IResult> SetWikiPublication(
        string guildId,
        SetWikiPublicationDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        [NotBody] VanityUrlService vanity,
        [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        // The bit that has existed since the wiki shipped and gated nothing. It is not in the
        // @everyone defaults, so enforcing it widens nothing for existing guilds.
        var canPublish = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.PublishWikiPublicly);
        if (!canPublish) return Results.Forbid();

        var guild = await ctx.Guilds.AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => new { g.Id, g.Kind })
            .FirstOrDefaultAsync();
        if (guild is null) return Results.NotFound();

        var wiki = await ctx.Wikis.FirstOrDefaultAsync(w => w.GuildId == guildId);
        if (wiki is null)
        {
            wiki = Wiki.Create(guildId);
            ctx.Wikis.Add(wiki);
        }

        if (string.IsNullOrWhiteSpace(dto.Slug))
        {
            var released = wiki.PublishedSlug;
            wiki.Unpublish();

            if (released is not null)
            {
                auditLog.Log(guildId, userId, AuditActionType.GuildUpdated, guildId,
                    new { WikiPublishedSlug = (string?)null, Previous = released });
            }

            return Results.Ok(await DescribePublicationAsync(
                guildId, wiki, await vanity.IsEntitledAsync(guildId, strict: false), ctx));
        }

        // A house manual is not something anybody meant to put on the open internet, and the
        // Household preset carries GuildFeatures.Wiki, so it would otherwise inherit the capability.
        if (guild.Kind == GuildKind.Household)
        {
            return Results.Json(
                new WikiPublicationErrorDto
                {
                    Error = WikiPublicationErrors.NotAvailable,
                    Message = "A household's wiki cannot be published publicly.",
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var normalized = VanitySlug.Normalize(dto.Slug);
        if (VanitySlug.Validate(normalized) is { } problem) return Results.BadRequest(problem);

        if (string.Equals(wiki.PublishedSlug, normalized, StringComparison.Ordinal))
            return Results.Ok(await DescribePublicationAsync(guildId, wiki, entitled: true, ctx));

        // Strict: publishing is the persistent artifact, so an unreadable plan denies rather than
        // grants. A payment step is also the cheapest spam filter available for free SEO-bearing
        // hosting on a real domain.
        if (!await vanity.IsEntitledAsync(guildId, strict: true))
        {
            return Results.Json(
                new { error = "wiki_publication_not_entitled", message = "This guild's plan does not include public wiki hosting." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var taken = await ctx.Wikis.AsNoTracking()
            .AnyAsync(w => w.PublishedSlug == normalized && w.GuildId != guildId);
        if (taken) return Results.Conflict("That wiki address is already taken.");

        wiki.Publish(normalized);

        auditLog.Log(guildId, userId, AuditActionType.GuildUpdated, guildId,
            new { WikiPublishedSlug = normalized });

        return Results.Ok(await DescribePublicationAsync(guildId, wiki, entitled: true, ctx));
    }

    /// <summary>Puts one page on the public host, or takes it off.</summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="dto">Whether the page is published.</param>
    /// <param name="permissionService">Resolves the caller's mask.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="user">The caller.</param>
    /// <param name="auditLog">Records who published what.</param>
    /// <returns>The page's publication state.</returns>
    [WolverinePut("/api/v1/guilds/{guildId}/wiki/pages/{pageId}/publication")]
    public async Task<IResult> SetWikiPagePublication(
        string guildId,
        string pageId,
        SetWikiPagePublicationDto dto,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user,
        [NotBody] AuditLogService auditLog)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var canPublish = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.PublishWikiPublicly);
        if (!canPublish) return Results.Forbid();

        var page = await ctx.WikiPages.FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (page is null) return Results.NotFound();

        if (dto.Published)
        {
            // Both refusals are fixable by the caller, so they name the fix in a code a client can
            // branch on rather than in prose it would have to match on.
            if (!WikiPublication.MayBePublished(page))
            {
                return Results.Json(
                    new WikiPublicationErrorDto
                    {
                        Error = WikiPublicationErrors.PagePrivate,
                        Message = "A page marked private inside the guild cannot be published publicly.",
                    },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // The column is capped at 2048 characters and validated nowhere else, and the comment on
            // it only claims covers point at uploaded storage. On a public page an arbitrary cover
            // is a per-visitor beacon aimed at an address the page author chose.
            if (!WikiPublication.IsInstanceHosted(page.CoverUrl, InstanceMediaHosts))
            {
                return Results.Json(
                    new WikiPublicationErrorDto
                    {
                        Error = WikiPublicationErrors.CoverNotHosted,
                        Message = "A published page's cover image must be hosted on this instance.",
                    },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            page.PublishedAt ??= DateTimeOffset.UtcNow;
        }
        else
        {
            page.PublishedAt = null;
        }

        // No RaiseUpdated: publishing is not an edit, and watchers should not be pinged as though
        // the prose had changed.
        auditLog.Log(guildId, userId, AuditActionType.GuildUpdated, pageId,
            new { WikiPagePublished = dto.Published });

        var wiki = await ctx.Wikis.AsNoTracking().FirstOrDefaultAsync(w => w.GuildId == guildId);
        var active = WikiPublication.IsPublic(wiki, page);

        return Results.Ok(new WikiPagePublicationDto
        {
            PageId = page.Id,
            Published = page.PublishedAt is not null,
            Active = active,
            PublishedAt = page.PublishedAt,
            Url = active ? PublishedPageUrl(wiki!.PublishedSlug!, page.Slug) : null,
        });
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

        // Two checks rather than one mask: the bits now live in two different enums, and both must
        // hold.
        var canReact =
            await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki) &&
            await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.AddReactions);
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
        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
        if (!canView) return Results.Forbid();

        var page = await ctx.WikiPages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pageId && p.GuildId == guildId);
        if (page is null) return Results.NotFound();
        if (!await CanSeeAsync(page, userId, guildId, permissionService)) return Results.NotFound();

        var canModerate = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ModerateWikiComments);

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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
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

        var canView = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.ViewWiki);
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
        var requiredPermission = isOwn ? ModulePermissions.ViewWiki : ModulePermissions.ModerateWikiComments;
        var canDelete = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
        if (!canDelete) return Results.Forbid();

        comment.RaiseDeleted();
        ctx.WikiComments.Remove(comment);

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Who may read a page marked private: whoever wrote it, and whoever may already rewrite it.
    /// </summary>
    private static async Task<bool> CanSeeAsync(
        WikiPage page, string userId, string guildId, GuildPermissionService permissionService) =>
        page.Visibility == WikiVisibility.Public
        || page.AuthorId == userId
        || await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, ModulePermissions.EditAnyWikiPage);

    /// <summary>The hostnames a published page's images may come from.</summary>
    private static IReadOnlyCollection<string> InstanceMediaHosts { get; } = BuildInstanceMediaHosts();

    private static List<string> BuildInstanceMediaHosts()
    {
        var hosts = new List<string>();

        foreach (var candidate in new[] { Env.GeneralConfiguration.InstanceUrl, Env.StorageConfiguration.PublicUrl })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                hosts.Add(uri.Host);
        }

        return hosts;
    }

    /// <summary>
    /// A published page's canonical address. Each wiki gets its own subdomain rather than a path
    /// segment, so the slug goes in front of the host - the path form still answers, but only as a
    /// permanent redirect to this.
    /// </summary>
    private static string PublishedPageUrl(string wikiSlug, string pageSlug)
    {
        var baseUrl = WikiSiteBaseUrl;
        var separator = baseUrl.IndexOf("://", StringComparison.Ordinal);

        // No scheme to split on should not produce a mangled link, so fall back to the path form.
        if (separator < 0) return $"{baseUrl}/{wikiSlug}/{pageSlug}";

        var scheme = baseUrl[..separator];
        var host = baseUrl[(separator + 3)..].TrimEnd('/');

        return $"{scheme}://{wikiSlug}.{host}/{pageSlug}";
    }

    /// <summary>Where the public wiki answers, honouring the gateway's own WIKI_DOMAIN override.</summary>
    private static string WikiSiteBaseUrl
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("WIKI_DOMAIN")?.Trim().TrimEnd('/');
            var instanceUrl = Env.GeneralConfiguration.InstanceUrl;

            if (string.IsNullOrEmpty(configured))
                return InstanceHosts.DeriveSiblingUrl(WikiSiteLabel, instanceUrl);

            if (configured.Contains("://", StringComparison.Ordinal)) return configured;

            var scheme = Uri.TryCreate(instanceUrl, UriKind.Absolute, out var uri)
                ? uri.Scheme
                : Uri.UriSchemeHttps;

            return $"{scheme}://{configured}";
        }
    }

    /// <summary>The publication state of a wiki, with its published-page count.</summary>
    private static async Task<WikiPublicationDto> DescribePublicationAsync(
        string guildId, Wiki? wiki, bool entitled, MicroserviceContext ctx)
    {
        var publishedPages = await ctx.WikiPages.AsNoTracking()
            .Where(p => p.GuildId == guildId)
            .Where(WikiPublication.IsPageOptedIn)
            .CountAsync();

        return new WikiPublicationDto
        {
            GuildId = guildId,
            Slug = wiki?.PublishedSlug,
            Entitled = entitled,
            Active = wiki?.PublishedSlug is not null && entitled,
            PublishedPageCount = publishedPages,
            PublishedAt = wiki?.PublishedAt,
        };
    }

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
