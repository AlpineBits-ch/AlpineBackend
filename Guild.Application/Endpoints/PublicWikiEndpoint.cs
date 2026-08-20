using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The anonymous read side of a published wiki. Nothing here takes a guild id: a public route that
/// accepted one would let anybody walk the id space to learn which guilds exist and which have
/// wikis, so every lookup starts from the slug the guild chose to publish under.
/// </summary>
public class PublicWikiEndpoint
{
    /// <summary>The index of a published wiki.</summary>
    /// <param name="slug">The wiki's published slug.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="vanity">The entitlement gate publishing shares with vanity URLs.</param>
    /// <returns>The wiki and its published pages, or 404.</returns>
    [AllowAnonymous]
    [WolverineGet("/api/v1/wiki/public/{slug}")]
    public async Task<IResult> GetPublicWiki(
        string slug,
        [NotBody] MicroserviceContext ctx,
        [NotBody] VanityUrlService vanity)
    {
        var published = await ResolveAsync(slug, ctx, vanity);
        if (published is null) return Results.NotFound();

        var categories = await ctx.WikiCategories.AsNoTracking()
            .Where(c => c.GuildId == published.GuildId)
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var pages = await ctx.WikiPages.AsNoTracking()
            .Where(p => p.GuildId == published.GuildId)
            .Where(WikiPublication.IsPageOptedIn)
            .OrderBy(p => p.Title)
            .Select(p => new { p.Slug, p.Title, p.Icon, p.CategoryId })
            .ToListAsync();

        return Results.Ok(new PublicWikiDto
        {
            Slug = published.Slug,
            GuildName = published.GuildName,
            GuildDescription = published.GuildDescription,
            Pages = pages.Select(p => new PublicWikiPageSummaryDto
            {
                Slug = p.Slug,
                Title = p.Title,
                Icon = p.Icon,
                Category = p.CategoryId is { } id ? categories.GetValueOrDefault(id) : null,
            }).ToList(),
        });
    }

    /// <summary>One published page.</summary>
    /// <param name="slug">The wiki's published slug.</param>
    /// <param name="pageSlug">The page's slug.</param>
    /// <param name="ctx">The Guild database.</param>
    /// <param name="vanity">The entitlement gate publishing shares with vanity URLs.</param>
    /// <returns>The page, or 404.</returns>
    [AllowAnonymous]
    [WolverineGet("/api/v1/wiki/public/{slug}/{pageSlug}")]
    public async Task<IResult> GetPublicWikiPage(
        string slug,
        string pageSlug,
        [NotBody] MicroserviceContext ctx,
        [NotBody] VanityUrlService vanity)
    {
        var published = await ResolveAsync(slug, ctx, vanity);
        if (published is null) return Results.NotFound();

        var page = await ctx.WikiPages.AsNoTracking()
            .Where(p => p.GuildId == published.GuildId && p.Slug == pageSlug)
            .Where(WikiPublication.IsPageOptedIn)
            .FirstOrDefaultAsync();

        // A page that exists but is not published is the same 404 as a page that does not exist. A
        // 403 would confirm it is there.
        if (page is null) return Results.NotFound();

        var category = page.CategoryId is null
            ? null
            : await ctx.WikiCategories.AsNoTracking()
                .Where(c => c.Id == page.CategoryId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync();

        // Only published targets: an internal link must not become an address on the public site
        // for a page the guild kept to itself.
        var linkedPages = await (
                from link in ctx.WikiPageLinks.AsNoTracking().Where(l => l.SourcePageId == page.Id)
                join target in ctx.WikiPages.AsNoTracking().Where(WikiPublication.IsPageOptedIn)
                    on link.TargetPageId equals target.Id
                where target.GuildId == published.GuildId
                select new { target.Id, target.Slug })
            .ToDictionaryAsync(t => t.Id, t => t.Slug);

        return Results.Ok(new PublicWikiPageDto
        {
            Slug = page.Slug,
            Title = page.Title,
            Content = page.Content,
            Icon = page.Icon,
            CoverUrl = page.CoverUrl,
            Category = category,
            UpdatedAt = page.UpdatedAt,
            GuildName = published.GuildName,
            WikiSlug = published.Slug,
            LinkedPages = linkedPages,
        });
    }

    /// <summary>What a slug resolves to once every gate in front of it has passed.</summary>
    private sealed record PublishedWiki(
        string GuildId, string Slug, string GuildName, string? GuildDescription);

    /// <summary>
    /// Turns a slug into a guild, or null for every reason a reader must not be able to tell apart:
    /// no such slug, the wiki module switched off, the plan lapsed, or a household.
    /// </summary>
    private static async Task<PublishedWiki?> ResolveAsync(
        string slug, MicroserviceContext ctx, VanityUrlService vanity)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var normalised = VanitySlug.Normalize(slug);
        if (VanitySlug.Validate(normalised) is not null) return null;

        var wiki = await ctx.Wikis.AsNoTracking()
            .Where(w => w.PublishedSlug == normalised)
            .Select(w => new { w.GuildId })
            .FirstOrDefaultAsync();

        if (wiki is null) return null;

        var guild = await ctx.Guilds.AsNoTracking()
            .Where(g => g.Id == wiki.GuildId)
            .Select(g => new { g.Id, g.Name, g.Description, g.Kind, g.Features })
            .FirstOrDefaultAsync();

        // The wiki row cascades with the guild, so this is belt and braces rather than a live case.
        if (guild is null) return null;

        if (!guild.Features.HasFlag(GuildFeatures.Wiki)) return null;
        if (guild.Kind == GuildKind.Household) return null;

        // Not strict, matching VanityUrlService.ResolveInviteAsync: a Billing outage must not blank
        // every published wiki on the instance, but a plan that has actually lapsed stops serving.
        if (!await vanity.IsEntitledAsync(guild.Id, strict: false)) return null;

        return new PublishedWiki(guild.Id, normalised, guild.Name, guild.Description);
    }
}
