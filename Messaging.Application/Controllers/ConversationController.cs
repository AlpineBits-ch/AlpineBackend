using System.Security.Claims;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Messaging.Application.Dtos;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ConversationDto = Messaging.Application.Dtos.Response.ConversationDto;

namespace Messaging.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/conversations")]
public class ConversationController(MicroserviceContext ctx, PrivacySettingsCache privacySettings) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetConversations([FromQuery] int offset, [FromQuery] int limit)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();


        var conversations = await ctx.Conversations
            .Include(c => c.Members)
            .Where(c => c.Members.Any(m => m.UserId == userId))
            .AsNoTracking()
            .Skip(offset)
            .Take(limit).ToListAsync();

        var dtos = conversations.SelectFacets<Conversation, ConversationDto>().ToList();
        await ApplyReadReceiptPrivacyAsync(dtos, userId);
        return Ok(dtos);
    }


    // GET welcomes moved to MlsEndpoints.GetWelcomes.


    [HttpGet("{id}")]
    public async Task<IActionResult> GetConversation(string id)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null) return BadRequest();


        var conversations = await ctx.Conversations.Include(c => c.Members).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Members.Any(m => m.UserId == userId));
        if(conversations is null) return NotFound();

        var dto = conversations.ToFacet<Conversation, ConversationDto>();
        await ApplyReadReceiptPrivacyAsync([dto], userId);
        return Ok(dto);
    }

    /// <summary>T2-18, the projection half.</summary>
    private async Task ApplyReadReceiptPrivacyAsync(IReadOnlyCollection<ConversationDto> conversations, string viewerUserId)
    {
        var memberIds = conversations
            .SelectMany(c => c.Members ?? [])
            .Select(m => m.UserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (memberIds.Count == 0) return;

        var settings = await privacySettings.GetAsync(memberIds.Append(viewerUserId));
        var viewerReceives = !settings.TryGetValue(viewerUserId, out var viewer) || viewer.SendReadReceipts;

        foreach (var member in conversations.SelectMany(c => c.Members ?? []))
        {
            if (string.Equals(member.UserId, viewerUserId, StringComparison.Ordinal)) continue;

            var memberEmits = !settings.TryGetValue(member.UserId, out var record) || record.SendReadReceipts;
            if (viewerReceives && memberEmits) continue;

            member.LastReadMessageId = null;
        }
    }
}
