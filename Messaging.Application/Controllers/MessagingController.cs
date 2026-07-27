using System.Security.Claims;
using Cassandra.Mapping;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Messaging.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/messaging")]
public class MessagingController(ScyllaContext scylla, MicroserviceContext ctx, IDistributedCache cache, ILogger<MessagingController> logger, IMessageBus bus, ConversationPermissionService conversationPermissionService) : ControllerBase
{
    [HttpGet("conversations/{conversationId}/messages")]
    public async Task<IActionResult> GetMessages(string conversationId, [FromQuery] int offset, [FromQuery] int limit)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            return BadRequest("Conversation ID is required");
        }
        
        if(User.FindFirst(ClaimTypes.NameIdentifier) is null) return Unauthorized();
        
        var permissions = await conversationPermissionService.HasPermission(User.FindFirstValue(ClaimTypes.NameIdentifier)!, conversationId);

        if(!permissions) return Forbid();


        try 
        {
            var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";
        
          
        
            var messageItems = await scylla.Mapper.FetchAsync<Message>(cql, conversationId, offset + limit);
        
            var result = messageItems
                .Skip(offset)
                .Take(limit)
                .OrderBy(m => m.CreatedAt) // Flip them back to chronological order
                .ToList();
            
            var reactionCql = "SELECT * FROM reactions WHERE context_id = ? AND message_id = ?";
            var reactionTasks = result.Select(m => 
                scylla.Mapper.FetchAsync<Reaction>(reactionCql, m.ContextId, m.Id));
    
            var reactionResults = await Task.WhenAll(reactionTasks);
    
            var reactionsByMessage = result
                .Zip(reactionResults, (m, reactions) => (m.Id, Reactions: reactions.ToList()))
                .ToDictionary(x => x.Id, x => x.Reactions);

            var messages = result.SelectFacets<Message, MessageDto>();

            foreach (var message in messages)
            {
                message.Reactions = reactionsByMessage.GetValueOrDefault(message.Id, []);
            }
            return Ok(messages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving messages");
            // Log the error (ctx.Logger or similar)
            return StatusCode(500, "Error retrieving messages");
        }
        
    }
    
    [HttpGet("channels/{channelId}/messages")]
    public async Task<IActionResult> GetMessagesForChannelAsync(string channelId, [FromQuery] int offset, [FromQuery] int limit)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return BadRequest("Conversation ID is required");
        }
        
        if(User.FindFirst(ClaimTypes.NameIdentifier) is null) return Unauthorized();


        var permissionResponse = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest()
            {
                UserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                ChannelId = channelId,
                Permission = ExternalPermission.ViewChannel
            });
        

        if(!permissionResponse.IsAllowed) return Forbid();


        try 
        {
            var cql = $"SELECT {Message.SelectColumns} FROM messages WHERE context_id = ? ORDER BY created_at DESC LIMIT ?";
        
          
        
            var messageItems = await scylla.Mapper.FetchAsync<Message>(cql, channelId, offset + limit);
        
            var result = messageItems
                .Skip(offset)
                .Take(limit)
                .OrderBy(m => m.CreatedAt) // Flip them back to chronological order
                .ToList();

            var reactionCql = "SELECT * FROM reactions WHERE context_id = ? AND message_id = ?";
            var reactionTasks = result.Select(m => 
                scylla.Mapper.FetchAsync<Reaction>(reactionCql, m.ContextId, m.Id));
    
            var reactionResults = await Task.WhenAll(reactionTasks);
    
            var reactionsByMessage = result
                .Zip(reactionResults, (m, reactions) => (m.Id, Reactions: reactions.ToList()))
                .ToDictionary(x => x.Id, x => x.Reactions);

            var messages = result.SelectFacets<Message, MessageDto>();

            foreach (var message in messages)
            {
                message.Reactions = reactionsByMessage.GetValueOrDefault(message.Id, []);
            }
            return Ok(messages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving messages");
            // Log the error (ctx.Logger or similar)
            return StatusCode(500, "Error retrieving messages");
        }
        
    }
}