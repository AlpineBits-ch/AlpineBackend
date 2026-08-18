using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// The instance operator's takedown for a published wiki. Deliberately checks no guild permission:
/// the HTTP publication routes are gated on PublishWikiPublicly, which a staff moderator does not
/// hold, and publishing puts content on the operator's own domain - so taking it down has to be
/// possible without joining the guild that put it there.
/// </summary>
public class ModerationUnpublishWikiHandler
{
    public static async Task<ModerationUnpublishWikiResponse> Handle(
        ModerationUnpublishWikiRequest request, MicroserviceContext ctx, AuditLogService auditLog)
    {
        var wiki = await ctx.Wikis.FirstOrDefaultAsync(w => w.PublishedSlug == request.Slug);
        if (wiki is null) return new ModerationUnpublishWikiResponse { Found = false };

        var guildName = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == wiki.GuildId)
            .Select(g => g.Name)
            .FirstOrDefaultAsync();

        var response = new ModerationUnpublishWikiResponse
        {
            Found = true,
            GuildId = wiki.GuildId,
            GuildName = guildName,
        };

        if (request.PageSlug is null)
        {
            // Already off is reported as success with Unpublished false, not as a failure: two
            // moderators working the same report must both be told the page is down.
            response.Unpublished = wiki.PublishedSlug is not null;
            wiki.Unpublish();

            auditLog.Log(wiki.GuildId, request.ActorUserId, AuditActionType.GuildUpdated, wiki.GuildId,
                new { WikiUnpublishedByOperator = true, request.Reason });

            return response;
        }

        var page = await ctx.WikiPages
            .FirstOrDefaultAsync(p => p.GuildId == wiki.GuildId && p.Slug == request.PageSlug);

        if (page is null) return new ModerationUnpublishWikiResponse { Found = false };

        response.PageTitle = page.Title;
        response.Unpublished = page.PublishedAt is not null;
        page.PublishedAt = null;

        auditLog.Log(wiki.GuildId, request.ActorUserId, AuditActionType.GuildUpdated, page.Id,
            new { WikiPageUnpublishedByOperator = true, request.Reason });

        return response;
    }
}
