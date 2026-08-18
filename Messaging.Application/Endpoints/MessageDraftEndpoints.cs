using System.Security.Claims;
using Echo.Realtime;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

/// <summary>
/// Server-side drafts: the unsent body for one person in one channel or conversation, so a long post
/// survives a refresh, a crash and a change of device.
/// </summary>
[Authorize]
public class MessageDraftEndpoints
{
    /// <summary>Pushed to the author's own connections when a draft is written.</summary>
    public const string DraftUpdatedEvent = "messaging.DraftUpdated";

    /// <summary>Pushed to the author's own connections when a draft is discarded.</summary>
    public const string DraftDeletedEvent = "messaging.DraftDeleted";

    /// <summary>The caller's draft for one context.</summary>
    /// <param name="contextId">The channel or conversation id, the same id the send path takes.</param>
    /// <param name="ctx">Messaging's database.</param>
    /// <param name="user">The caller.</param>
    /// <returns>The draft, or 404 when nothing has been typed there.</returns>
    [WolverineGet("/api/v1/messaging/drafts/{contextId}")]
    public static async Task<IResult> GetDraft(
        string contextId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        // Deliberately not gated on permission to the context. This is the caller's own text, and a
        // draft they can no longer send is exactly the one they most need to read back and copy
        // somewhere else.
        var draft = await ctx.MessageDrafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ContextId == contextId);

        return draft is null ? Results.NotFound() : Results.Ok(MessageDraftDto.From(draft));
    }

    /// <summary>Replaces the caller's draft for one context, or discards it when the body is empty.</summary>
    /// <param name="contextId">The channel or conversation id, the same id the send path takes.</param>
    /// <param name="dto">The draft as the client last had it.</param>
    /// <param name="ctx">Messaging's database.</param>
    /// <param name="user">The caller.</param>
    /// <param name="bus">Used to check the caller may still write in a guild channel.</param>
    /// <param name="conversationPermissions">The same check for a DM or group.</param>
    /// <param name="hub">Realtime, addressed only ever at the author's own connections.</param>
    /// <returns>The stored draft, or 204 when an empty body discarded it.</returns>
    [WolverinePut("/api/v1/messaging/drafts/{contextId}")]
    public static async Task<IResult> UpsertDraft(
        string contextId, UpsertMessageDraftDto dto, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus,
        [NotBody] ConversationPermissionService conversationPermissions,
        [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(contextId)) return Results.BadRequest("contextId is required");

        // Only the instance's hard ceiling, not the guild's plan ceiling. A draft is not a send, and
        // refusing to hold text because the guild dropped to a lower plan would delete the very post
        // its author is in the middle of trimming to fit.
        var length = MessageLength.Of(dto.Content);
        if (length > MessageLengthPolicy.HardCeilingCharacters)
        {
            return Results.Json(
                new
                {
                    error = MessageLengthPolicy.TooLongError,
                    maxLength = MessageLengthPolicy.HardCeilingCharacters,
                    length,
                },
                statusCode: MessageLengthPolicy.TooLongStatusCode);
        }

        var isConversation = await ctx.Conversations.AnyAsync(c => c.Id == contextId);

        if (!await MayWriteAsync(contextId, isConversation, userId, bus, conversationPermissions))
            return Results.Forbid();

        var existing = await ctx.MessageDrafts
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ContextId == contextId);

        // An empty box is not a draft. Clients save on a debounce, so the delete has to be the same
        // call as the save or clearing the composer would leave the old text behind on every device.
        // An attachment with no text still counts: the upload is what would be lost.
        if (string.IsNullOrEmpty(dto.Content) && dto.Attachments.Count == 0)
        {
            if (existing is not null) ctx.MessageDrafts.Remove(existing);

            await NotifyAsync(hub, userId, DraftDeletedEvent,
                new { contextId, deviceId = dto.SenderDeviceId });

            return Results.NoContent();
        }

        if (existing is null)
        {
            existing = MessageDraft.Create(
                userId,
                isConversation ? null : contextId,
                isConversation ? contextId : null,
                dto.Content,
                dto.InReplyTo,
                dto.Attachments);

            ctx.MessageDrafts.Add(existing);
        }
        else
        {
            // Replaced wholesale rather than appended, and there is no history: the client owns the
            // text and the server holds only its latest state.
            existing.Replace(dto.Content, dto.InReplyTo, dto.Attachments);
        }

        await NotifyAsync(hub, userId, DraftUpdatedEvent, MessageDraftDto.From(existing, dto.SenderDeviceId));

        return Results.Ok(MessageDraftDto.From(existing, dto.SenderDeviceId));
    }

    /// <summary>Discards the caller's draft for one context.</summary>
    /// <param name="contextId">The channel or conversation id, the same id the send path takes.</param>
    /// <param name="deviceId">Which device discarded it, so that device can ignore its own echo.</param>
    /// <param name="ctx">Messaging's database.</param>
    /// <param name="user">The caller.</param>
    /// <param name="hub">Realtime, addressed only ever at the author's own connections.</param>
    /// <returns>204 whether or not there was anything to discard.</returns>
    [WolverineDelete("/api/v1/messaging/drafts/{contextId}")]
    public static async Task<IResult> DeleteDraft(
        string contextId, [FromQuery] string? deviceId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] IHubContext<EchoRealtimeHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(contextId)) return Results.BadRequest("contextId is required");

        var draft = await ctx.MessageDrafts
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ContextId == contextId);

        if (draft is not null) ctx.MessageDrafts.Remove(draft);

        // Discarding text the caller can no longer send needs no permission for the same reason
        // reading it back does not.
        await NotifyAsync(hub, userId, DraftDeletedEvent, new { contextId, deviceId });

        return Results.NoContent();
    }

    /// <summary>
    /// The author's own connections and nobody else's. A draft is private to whoever typed it - no
    /// channel fan-out, no push, no unread effect, and no search index entry.
    /// </summary>
    private static Task NotifyAsync(
        IHubContext<EchoRealtimeHub> hub, string userId, string eventName, object payload) =>
        hub.Clients.User(userId).SendAsync(eventName, payload);

    /// <summary>Whether the caller could still post what they are storing.</summary>
    private static async Task<bool> MayWriteAsync(
        string contextId, bool isConversation, string userId,
        IMessageBus bus, ConversationPermissionService conversationPermissions)
    {
        if (isConversation) return await conversationPermissions.HasPermission(userId, contextId);

        var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest
            {
                ChannelId = contextId,
                UserId = userId,
                Permission = ExternalPermission.SendMessages,
            });

        return response.IsAllowed;
    }
}
