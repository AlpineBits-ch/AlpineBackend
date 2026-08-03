using System.Security.Claims;
using Echo.Realtime;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Events.Reactions;
using Messaging.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

public class ReactionsEndpoint
{
    /// <summary>
    /// The context is taken from the target message, never from the request body, and the caller is
    /// authorized against it - the same shape MessagingEndpoints.PinMessage uses.
    /// </summary>
    [Authorize]
    [WolverinePost("/api/v1/messages/{messageId}/reactions")]
    public async Task<(IResult, ReactionCreated?)> AddReaction(string messageId, CreateReactionDto dto, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] IHubContext<EchoRealtimeHub> hubContext, [NotBody] IMessageBus bus,
        [NotBody] ConversationPermissionService conversationPermissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return (Results.NotFound(), null);

        var channelId = message.ChannelId;
        var conversationId = message.ConversationId;

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest
                {
                    ChannelId = channelId,
                    UserId = userId,
                    Permission = ExternalPermission.AddReactions,
                });

            if (!permission.IsAllowed) return (Results.Forbid(), null);
        }
        else if (!string.IsNullOrWhiteSpace(conversationId))
        {
            if (!await conversationPermissionService.HasPermission(userId, conversationId))
                return (Results.Forbid(), null);
        }
        else
        {
            return (Results.NotFound(), null);
        }

        var emojiName = dto.Reaction;

        if (!string.IsNullOrWhiteSpace(dto.EmojiId))
        {
            if (string.IsNullOrWhiteSpace(channelId))
                return (Results.BadRequest("Custom emoji reactions are only supported in guild channels."), null);

            var emojiResponse = await bus.InvokeAsync<GetGuildEmojiResponse>(new GetGuildEmojiRequest
            {
                ChannelId = channelId,
                EmojiId = dto.EmojiId,
            });

            if (!emojiResponse.Found) return (Results.NotFound("Emoji not found in this guild."), null);
            emojiName = emojiResponse.Name;
        }

        Reaction reaction;
        try
        {
            reaction = Reaction.Create(new CreateReactionParams()
            {
                MessageId = messageId,
                UserId = userId,
                Emoji = emojiName,
                ChannelId = channelId,
                ConversationId = conversationId,
                EmojiId = dto.EmojiId,
            });
        }
        catch (ArgumentException ex)
        {
            return (Results.BadRequest(ex.Message), null);
        }

        await repo.AddReactionAsync(reaction);

        return (Results.Accepted(),new ReactionCreated()
        {
            CorrelationId = messageId,
            Emoji = emojiName,
            MessageId = messageId,
            UserId = userId,
            ChannelId = channelId,
            ConversationId = conversationId,
            EmojiId = dto.EmojiId,
        });
    }

    [Authorize]
    [WolverineDelete("/api/v1/messages/{messageId}/reactions")]
    public async Task<(IResult, ReactionRemoved?)> RemoveReaction(string messageId, RemoveReactionDto dto, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] IHubContext<EchoRealtimeHub> hubContext, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        // Context resolved from the message rather than the body, matching AddReaction.
        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return (Results.NotFound(), null);

        await repo.RemoveReactionAsync(message.ContextId, messageId, dto.Reaction, userId);

        return (Results.Ok(), new ReactionRemoved
        {
            MessageId = messageId,
            Emoji = dto.Reaction,
            UserId = userId,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
        });
    }
}