using System.Security.Claims;
using Facet.Extensions;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Domain.Events.Conversation;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;
using ConversationAggregate = Messaging.Domain.Aggregates.Conversation;

namespace Messaging.Application.Controllers;

/// <summary>
/// A group conversation's icon. A controller rather than a Wolverine endpoint because the upload is
/// multipart, the same reason GuildIconController is one.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/conversations/{conversationId}/icon")]
public class ConversationIconController(
    MicroserviceContext ctx,
    ConversationIconService icons,
    IMessageBus bus) : ControllerBase
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif",
    };

    private const long MaxIconBytes = 8 * 1024 * 1024;

    /// <summary>Streams the icon back rather than redirecting to a presigned URL: a cross-origin
    /// redirect would either drop the bearer or invalidate the signature.</summary>
    [HttpGet]
    public async Task<IActionResult> GetIcon(string conversationId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var conversation = await LoadForMemberAsync(conversationId, userId, ct);
        if (conversation is null) return NotFound();
        if (conversation.IconUpdatedAt is null) return NotFound();

        var icon = await icons.GetAsync(conversationId, ct);
        if (icon is null) return NotFound();

        Response.Headers.CacheControl = "private, max-age=3600";
        return File(icon.Content, icon.ContentType);
    }

    [HttpPost]
    public async Task<IActionResult> UploadIcon(string conversationId, [FromForm] IFormFile file, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        if (file is null || file.Length == 0) return BadRequest("A file is required.");
        if (file.Length > MaxIconBytes) return BadRequest($"File exceeds the {MaxIconBytes / (1024 * 1024)}MB limit.");
        if (!AllowedContentTypes.Contains(file.ContentType)) return BadRequest("Unsupported image type.");

        var conversation = await LoadForMemberAsync(conversationId, userId, ct);
        if (conversation is null) return NotFound();
        if (conversation.Members.Count <= 2) return BadRequest("Only a group conversation can have an icon.");

        await icons.UploadAsync(conversationId, file, ct);

        conversation.IconUpdatedAt = DateTimeOffset.UtcNow;
        conversation.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);

        await AnnounceAsync(conversation, userId, string.Empty);
        return Ok(conversation.ToFacet<ConversationAggregate, ConversationDto>());
    }

    [HttpDelete]
    public async Task<IActionResult> RemoveIcon(string conversationId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var conversation = await LoadForMemberAsync(conversationId, userId, ct);
        if (conversation is null) return NotFound();
        if (conversation.IconUpdatedAt is null) return Ok(conversation.ToFacet<ConversationAggregate, ConversationDto>());

        await icons.DeleteAsync(conversationId, ct);

        conversation.IconUpdatedAt = null;
        conversation.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);

        await AnnounceAsync(conversation, userId, "removed");
        return Ok(conversation.ToFacet<ConversationAggregate, ConversationDto>());
    }

    private Task<ConversationAggregate?> LoadForMemberAsync(
        string conversationId, string userId, CancellationToken ct) =>
        ctx.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.Members.Any(m => m.UserId == userId), ct);

    private async Task AnnounceAsync(ConversationAggregate conversation, string userId, string content)
    {
        await ConversationEndpoints.WriteSystemMessageAsync(
            bus, conversation.Id, userId, ContractMessageType.GroupIconChanged, content);

        await bus.PublishAsync(new ConversationUpdated
        {
            ConversationId = conversation.Id,
            CorrelationId = conversation.Id,
            Name = conversation.Name,
            IconUpdatedAt = conversation.IconUpdatedAt,
        });
    }
}
