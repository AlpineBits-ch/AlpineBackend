using System.Security.Claims;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Messaging.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/messaging")]
public class MessagingController(IMessageRepository repo, ILogger<MessagingController> logger, IMessageBus bus, ConversationPermissionService conversationPermissionService) : ControllerBase
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
            // Routed through IMessageRepository (not a direct ScyllaContext query) so this works
            // identically whether the deployment uses Scylla or the Postgres/EF Core fallback
            // (see MessagingInfrastructure's UseScyllaDb switch) - this endpoint previously always
            // hit Scylla directly regardless of that setting, throwing a NullReferenceException on
            // any self-hosted deployment (or test harness) that disables it.
            var (result, reactionsByMessage) = await repo.GetMessagesByConversationIdAsync(conversationId, limit, offset);

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
            var (result, reactionsByMessage) = await repo.GetMessagesByChannelIdAsync(channelId, limit, offset);

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
            return StatusCode(500, "Error retrieving messages");
        }

    }
}