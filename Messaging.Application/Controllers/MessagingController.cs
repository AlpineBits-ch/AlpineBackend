using System.Security.Claims;
using Facet.Extensions;
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
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    /// <summary>
    /// [FromQuery] int binds an omitted/blank limit to 0, which Scylla rejects outright ("LIMIT
    /// must be strictly positive") - a 500 on a request that is merely under-specified.
    /// </summary>
    private static (int Offset, int Limit) NormalizePaging(int offset, int limit) =>
        (Math.Max(0, offset), limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize));

    /// <summary>
    /// Resolves the optional cursor triplet into a repository query, or null when the caller
    /// supplied none - in which case the legacy offset path is used unchanged.
    /// </summary>
    private static MessagePageQuery? BuildCursorQuery(string contextId, string? before, string? after, string? around, int limit)
    {
        var (_, size) = NormalizePaging(0, limit);

        if (!string.IsNullOrWhiteSpace(before))
            return new MessagePageQuery { ContextId = contextId, AnchorMessageId = before, Direction = MessageCursorDirection.Before, Limit = size };

        if (!string.IsNullOrWhiteSpace(after))
            return new MessagePageQuery { ContextId = contextId, AnchorMessageId = after, Direction = MessageCursorDirection.After, Limit = size };

        if (!string.IsNullOrWhiteSpace(around))
            return new MessagePageQuery { ContextId = contextId, AnchorMessageId = around, Direction = MessageCursorDirection.Around, Limit = size };

        return null;
    }

    [HttpGet("conversations/{conversationId}/messages")]
    public async Task<IActionResult> GetMessages(string conversationId, [FromQuery] int offset, [FromQuery] int limit,
        [FromQuery] string? before = null, [FromQuery] string? after = null, [FromQuery] string? around = null)
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
            var cursor = BuildCursorQuery(conversationId, before, after, around, limit);

            // Response shape is identical on both paths, so a client can adopt cursors without any
            // parsing change - it just stops sending offset.
            var (result, reactionsByMessage) = cursor is not null
                ? await repo.GetMessagePageByCursorAsync(cursor)
                : await GetByOffsetAsync();

            async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetByOffsetAsync()
            {
                var (page, size) = NormalizePaging(offset, limit);
                return await repo.GetMessagesByConversationIdAsync(conversationId, size, page);
            }

            var messages = result.SelectFacets<Message, MessageDto>();

            foreach (var message in messages)
            {
                message.Reactions = reactionsByMessage.GetValueOrDefault(message.Id, []).SelectFacets<Reaction, ReactionDto>().ToList();
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
    public async Task<IActionResult> GetMessagesForChannelAsync(string channelId, [FromQuery] int offset, [FromQuery] int limit,
        [FromQuery] string? before = null, [FromQuery] string? after = null, [FromQuery] string? around = null)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return BadRequest("Conversation ID is required");
        }

        if(User.FindFirst(ClaimTypes.NameIdentifier) is null) return Unauthorized();


        // ViewChannel *and* ReadMessageHistory - this is the backlog, which is the whole thing the
        // second bit exists to gate separately. See MessageHistoryAccess for why both are asked.
        if (!await MessageHistoryAccess.MayReadAsync(channelId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, bus))
            return Forbid();

        try
        {
            var cursor = BuildCursorQuery(channelId, before, after, around, limit);

            var (result, reactionsByMessage) = cursor is not null
                ? await repo.GetMessagePageByCursorAsync(cursor)
                : await GetByOffsetAsync();

            async Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetByOffsetAsync()
            {
                var (page, size) = NormalizePaging(offset, limit);
                return await repo.GetMessagesByChannelIdAsync(channelId, size, page);
            }

            var messages = result.SelectFacets<Message, MessageDto>();

            foreach (var message in messages)
            {
                message.Reactions = reactionsByMessage.GetValueOrDefault(message.Id, []).SelectFacets<Reaction, ReactionDto>().ToList();
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