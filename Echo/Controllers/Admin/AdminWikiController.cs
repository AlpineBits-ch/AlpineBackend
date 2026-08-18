using Echo.Domain.Entities.Moderation;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Echo.Sites;
using Echo.Wiki;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Echo.Controllers.Admin;

/// <summary>
/// What the instance publishes on the wiki host, and how it stops.
///
/// A guild owner takes their own pages down through the guild's publication routes. This is the
/// other direction: publishing puts prose on the operator's own domain, so the operator needs a way
/// to take it off without joining the guild that put it there, and without holding a permission
/// inside it.
/// </summary>
[Authorize]
[Route("api/v1/admin/wiki")]
public class AdminWikiController(
    MicroserviceContext context,
    StaffAccess staff,
    WikiServiceClient wiki,
    IMessageBus bus,
    ILogger<AdminWikiController> logger)
    : AdminControllerBase(context, staff)
{
    /// <summary>Shows exactly what an address serves anonymously.</summary>
    [HttpGet]
    public async Task<IActionResult> LookupAsync([FromQuery] string? address, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var host = WikiHosting.Host;
        if (!WikiAddresses.TryParse(address, host, out var parsed))
            return Failure(400, "wiki_address_invalid", "That is not the address of a published wiki page.");

        var origin = $"{SiteHost.Scheme}://{WikiHost.HostFor(parsed.Slug, host)}";

        if (parsed.PageSlug is { } pageSlug)
        {
            var page = await wiki.GetPageAsync(parsed.Slug, pageSlug, ct);
            if (!page.ServiceReachable) return GuildUnavailable();

            if (page.Value is not { } found)
                return Failure(404, "wiki_page_not_published", "Nothing is published at that address.");

            return Ok(new
            {
                slug = parsed.Slug,
                pageSlug,
                url = $"{origin}/{found.Slug}",
                guildName = found.GuildName,
                title = found.Title,
                category = found.Category,
                coverUrl = found.CoverUrl,
                updatedAt = found.UpdatedAt,
                // The body as the public host renders it. A moderator deciding whether to take a
                // page down is deciding about this text, and sending them to the live page to read
                // it means deciding from memory once it is gone.
                content = found.Content,
            });
        }

        var index = await wiki.GetWikiAsync(parsed.Slug, ct);
        if (!index.ServiceReachable) return GuildUnavailable();

        if (index.Value is not { } wikiFound)
            return Failure(404, "wiki_not_published", "Nothing is published at that address.");

        return Ok(new
        {
            slug = parsed.Slug,
            pageSlug = (string?)null,
            url = origin,
            guildName = wikiFound.GuildName,
            guildDescription = wikiFound.GuildDescription,
            pages = wikiFound.Pages.Select(p => new
            {
                p.Slug,
                p.Title,
                p.Category,
                url = $"{origin}/{p.Slug}",
            }),
        });
    }

    /// <summary>Takes a page, or a whole wiki, off the public host.</summary>
    [HttpPost("unpublish")]
    public async Task<IActionResult> UnpublishAsync(
        [FromBody] UnpublishWikiRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        var host = WikiHosting.Host;
        if (!WikiAddresses.TryParse(request.Address, host, out var parsed))
            return Failure(400, "wiki_address_invalid", "That is not the address of a published wiki page.");

        // Same rule as closing a report: a takedown is a decision somebody may have to answer for.
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Failure(400, "reason_required", "Say why this is being taken down.");

        ModerationUnpublishWikiResponse response;
        try
        {
            response = await bus.InvokeAsync<ModerationUnpublishWikiResponse>(
                new ModerationUnpublishWikiRequest
                {
                    Slug = parsed.Slug,
                    PageSlug = parsed.PageSlug,
                    ActorUserId = actor.UserId,
                    Reason = request.Reason,
                });
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "The guild service did not answer a takedown of {WikiAddress} by {ActorUserId}.",
                parsed.ToString(), actor.UserId);

            return GuildUnavailable();
        }

        if (!response.Found)
            return Failure(404, "wiki_not_published", "Nothing is published at that address.");

        Audit(actor,
            parsed.PageSlug is null
                ? ModerationAuditActions.WikiUnpublished
                : ModerationAuditActions.WikiPageUnpublished,
            parsed.ToString(),
            $"{request.Reason!.Trim()} (guild {response.GuildId})");

        await Db.SaveChangesAsync(ct);

        logger.LogWarning(
            "{ActorUserId} took {WikiAddress} off the public wiki host (guild {GuildId})",
            actor.UserId, parsed.ToString(), response.GuildId);

        return Ok(new
        {
            slug = parsed.Slug,
            pageSlug = parsed.PageSlug,
            // False when it was already off, which is an outcome rather than a failure - two
            // moderators working the same report should both see it succeed.
            unpublished = response.Unpublished,
            guildId = response.GuildId,
            guildName = response.GuildName,
            title = response.PageTitle,
            // Only the origin stops serving. Whatever fetched it before still has its copy, and a
            // moderator promising a reporter otherwise is promising something nobody can deliver.
            cacheSeconds = WikiHosting.PageCacheSeconds,
        });
    }

    /// <summary>
    /// The guild service holds the publication state, so a takedown it did not acknowledge did not
    /// happen. Reported as a retry rather than as a refusal.
    /// </summary>
    private IActionResult GuildUnavailable() =>
        Failure(503, "wiki_service_unavailable",
            "The guild service did not answer. Nothing was changed - try again in a moment.");
}

/// <summary>An operator's takedown of something on the public wiki host.</summary>
public class UnpublishWikiRequest
{
    /// <summary>A slug, a slug and page, or any wiki URL.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Why, in the moderator's words. Required.</summary>
    public string? Reason { get; set; }
}
