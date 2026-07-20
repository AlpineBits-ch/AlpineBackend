using System.Security.Claims;
using Echo.Realtime;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Events.Reactions;
using Messaging.Infrastructure.Persistence;
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
    public async Task<(IResult, ReactionCreated?)> AddReaction(string messageId, CreateReactionDto dto, [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user, 
        [NotBody] IHubContext<EchoRealtimeHub> hubContext, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);
        
        var reaction = Reaction.Create(new CreateReactionParams()
        {
            MessageId = messageId,
            UserId = userId,
            Emoji = dto.Reaction,
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
        });
        
        await ctx.Mapper.InsertAsync(reaction);
        
        return (Results.Accepted(),new ReactionCreated()
        {
            CorrelationId = messageId,
            Emoji = dto.Reaction,
            MessageId = messageId,
            UserId = userId,
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
        });
    }

    [Authorize]
    [WolverineDelete("/api/v1/messages/{messageId}/reactions")]
    public async Task<IResult> RemoveReaction(string messageId, RemoveReactionDto dto, [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user,
        [NotBody] IHubContext<EchoRealtimeHub> hubContext, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        
        await ctx.Mapper.DeleteAsync<Reaction>(
            "WHERE context_id = ? AND message_id = ? AND emoji = ? AND user_id = ?",
            dto.ContextId, messageId, dto.Reaction, userId);
        
        return Results.Ok();
    }
}