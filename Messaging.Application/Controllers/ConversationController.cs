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


    // GET welcomes moved to MlsEndpoints.GetWelcomes. The version that lived here deleted every
    // Welcome for the user on read, which meant (a) a device drained its siblings' Welcomes, which
    // are keyed to leaves it does not hold, and (b) any failure between the fetch and the join lost
    // the only copy of a single-use init key, permanently locking that device out of the group.


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

    /// <summary>
    /// T2-18, the projection half.
    ///
    /// <para><c>ConversationMemberDto.LastReadMessageId</c> is a read receipt - it says "this person
    /// has read up to here", to everybody else in the room. The emit-site gate in
    /// <c>UpdateConversationReadHandler</c> keeps the realtime event from going out, but the stored
    /// value is projected onto every conversation read, so it has to be blanked here too or the
    /// setting is trivially defeated by polling <c>GET /api/v1/conversations</c>.</para>
    ///
    /// <para>Reciprocal, exactly as at the emit site: a viewer who does not send receipts sees
    /// nobody else's, and a member who does not send receipts is hidden from everyone. The caller's
    /// own row is never touched - it is their own read position, and the client needs it to place
    /// the unread divider.</para>
    ///
    /// <para>Blanked rather than omitted: the field is part of an existing response shape and
    /// removing it would break v1 clients that read it unconditionally.</para>
    /// </summary>
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
