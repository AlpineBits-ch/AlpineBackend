using System.Security.Claims;
using Facet.Extensions;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

[Authorize]
public class SearchEndpoint
{
    [WolverineGet("/api/v1/messaging/search")]
    public async Task<IResult> SearchMessages([FromQuery] string query, [FromQuery] string? channelId, [FromQuery] string? conversationId,
        [FromQuery] int limit, [NotBody] MicroserviceContext db, [NotBody] IMessageRepository repo,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(query)) return Results.BadRequest("query is required");
        if (string.IsNullOrWhiteSpace(channelId) && string.IsNullOrWhiteSpace(conversationId)) return Results.BadRequest("channelId or conversationId is required");

        if (limit is <= 0 or > 50) limit = 25;

        IQueryable<MessageSearchEntry> scoped;
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            // Search is a history read wearing a query string: every hit it can return is a message
            // that has already been sent, so withholding the backlog and then serving it back one
            // keyword at a time would make ReadMessageHistory decorative.
            if (!await MessageHistoryAccess.MayReadAsync(channelId, userId, bus)) return Results.Forbid();
            scoped = db.MessageSearchEntries.Where(e => e.ChannelId == channelId);
        }
        else
        {
            if (!await conversationPermissionService.HasPermission(userId, conversationId!)) return Results.Forbid();
            scoped = db.MessageSearchEntries.Where(e => e.ConversationId == conversationId);
        }

        var matchIds = await scoped
            .Where(e => e.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .OrderByDescending(e => e.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
            .Take(limit)
            .Select(e => e.MessageId)
            .ToListAsync();

        var messages = await Task.WhenAll(matchIds.Select(repo.GetMessageAsync));

        return Results.Ok(messages.Where(m => m is not null).Select(m => m!.ToFacet<Message, MessageDto>()));
    }
}
