using System.Security.Claims;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Messaging.Application.Dtos;
using Messaging.Application.Dtos.Request;
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
public class ConversationController(MicroserviceContext ctx) : ControllerBase
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
        return Ok(conversations.SelectFacets<Conversation, ConversationDto>());
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
        
        return Ok(conversations.ToFacet<Conversation, ConversationDto>());
    }
    
}