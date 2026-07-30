using System.Security.Claims;
using Echo.Realtime;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
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
    [Authorize]
    [WolverinePost("/api/v1/messages/{messageId}/reactions")]
    public async Task<(IResult, ReactionCreated?)> AddReaction(string messageId, CreateReactionDto dto, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] IHubContext<EchoRealtimeHub> hubContext, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);

        var emojiName = dto.Reaction;

        if (!string.IsNullOrWhiteSpace(dto.EmojiId))
        {
            if (string.IsNullOrWhiteSpace(dto.ChannelId))
                return (Results.BadRequest("Custom emoji reactions are only supported in guild channels."), null);

            var emojiResponse = await bus.InvokeAsync<GetGuildEmojiResponse>(new GetGuildEmojiRequest
            {
                ChannelId = dto.ChannelId,
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
                ChannelId = dto.ChannelId,
                ConversationId = dto.ConversationId,
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
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
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

        await repo.RemoveReactionAsync(dto.ContextId, messageId, dto.Reaction, userId);

        return (Results.Ok(), new ReactionRemoved
        {
            MessageId = messageId,
            Emoji = dto.Reaction,
            UserId = userId,
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
        });
    }
}