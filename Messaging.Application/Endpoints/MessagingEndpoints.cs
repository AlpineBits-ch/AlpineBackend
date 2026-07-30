using System.Security.Claims;
using System.Text;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Commands;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

[Authorize]

public class MessagingEndpoints
{
    [WolverinePost("/api/v1/messaging")]
    public async Task<(IResult, MessageCreated?)> CreateMessage(CreateMessageDto dto,  [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext context, [NotBody] IMessageBus bus, [NotBody] IDistributedCache cache)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return (Results.Unauthorized(), null);

        var authorIdType = user.FindFirstValue("user_type") == "Bot" ? AuthorIdType.Bot : AuthorIdType.User;

        if(string.IsNullOrWhiteSpace(dto.ConversationId) && string.IsNullOrWhiteSpace(dto.ChannelId)) return (Results.BadRequest(), null);


        if (!string.IsNullOrWhiteSpace(dto.ChannelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = dto.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.SendMessages
                });

            if(!response.IsAllowed) return (Results.Forbid(), null);

            // Bots/webhooks intentionally bypass auto-mod - a guild that installs a bot has
            // already made an explicit trust decision about what it posts.
            if (authorIdType != AuthorIdType.Bot)
            {
                var blockedReason = await AutoModeration.CheckAsync(dto.ChannelId, userId, dto.Content, cache, bus);
                if (blockedReason is not null)
                {
                    await bus.PublishAsync(new Guild.Contracts.Bus.Events.AutoModTriggeredEvent
                    {
                        ChannelId = dto.ChannelId,
                        UserId = userId,
                        Reason = blockedReason,
                    });

                    return (Results.Json(new { error = "automod_blocked", reason = blockedReason }, statusCode: StatusCodes.Status403Forbidden), null);
                }
            }
        }
        else
        {
            var conversation = await context.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == dto.ConversationId);
            if(conversation is null) return (Results.NotFound(), null);
        
            if(conversation.Members.All(m => m.UserId != userId))
            {
                return (Results.Forbid(), null);
            }   
            conversation.UpdatedAt = DateTime.UtcNow;

        }
        
       
        
        var attachments = (await context.Attachments.AsNoTracking().Where(a => dto.Attachments.Contains(a.Id)).ToListAsync()).Select(a => new MinimalAttachmentContract()
        {
            Id = a.Id,
            FileName = a.FileName,
            ContentType = a.ContentType,
            ThumbnailUrl = "https://api.venta.gg/api/v1/messaging/attachments/" + a.Id + "/thumbnail",
            ThumbnailId = a.ThumbnailId
        }).ToList();


        var encryptionState = MessageEncryptionState.Plain;

        if (dto.EncryptionState == Domain.Enums.MessageEncryptionState.Encrypted)
        {
            encryptionState = MessageEncryptionState.Encrypted;
        }
        
        var message = await bus.InvokeAsync<Message>(new CreateMessageCommand()
        {
            AuthorId = userId,
            AuthorIdType = authorIdType,
            Content = Encoding.UTF8.GetBytes(dto.Content),
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
            Attachments = attachments,
            InReplyTo = dto.InReplyTo,
            Mentions = dto.Mentions.ToList(),
            RoleMentions = dto.RoleMentions.ToList(),
            MentionsEveryone = dto.MentionsEveryone,
            MentionsHere = dto.MentionsHere,
            EncryptionState = encryptionState,
            MlsEpoch = dto.MlsEpoch,
            MlsSequenceNumber = dto.MlsSequenceNumber,
            SenderDeviceId = dto.SenderDeviceId
        });
        
       
        
        

        return (Results.Created($"/api/v1/messaging/{message.Id}", message.ToFacet<Message, MessageDto>()),
            new MessageCreated()
            {
                MessageId = message.Id,
                ChannelId = dto.ChannelId,
                ConversationId = dto.ConversationId,
                ContextId = message.ContextId,
                Content = message.Content,
                CorrelationId = message.ContextId,
                AuthorId = userId,
                Attachments = message.Attachments,
                InReplyTo = message.InReplyTo,
                EncryptionState = message.EncryptionState,
                Mentions = message.Mentions,
                MlsSequenceNumber = message.MlsSequenceNumber,
                SenderDeviceId = message.SenderDeviceId,
                MlsEpoch = message.MlsEpoch,
            });
    }

    [WolverineDelete("/api/v1/messaging/{messageId}")]
    public async Task<(IResult, MessageDeleted?)> DeleteMessage(string messageId, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return (Results.Unauthorized(), null);

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return (Results.NotFound(), null);
        if (message.AuthorId != userId)
        {
            return (Results.Forbid(), null);
        }

        await repo.DeleteMessageAsync(message);
        return (Results.Accepted(), new MessageDeleted()
        {
            MessageId = messageId,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
        });
    }

    [WolverinePut("/api/v1/messaging/{messageId}")]
    public async Task<IResult> UpdateMessageAsync(string messageId, UpdateMessageDto dto, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var result = await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = messageId,
            RequestingAuthorId = userId,
            Content = Encoding.UTF8.GetBytes(dto.Content),
        });

        if (result.NotFound) return Results.NotFound();
        if (result.Forbidden) return Results.Forbid();

        return Results.Accepted(value: new { messageId, content = dto.Content });
    }

    [WolverinePost("/api/v1/messaging/{messageId}/pin")]
    public async Task<IResult> PinMessage(string messageId, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(message.ChannelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = message.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.PinMessages
                });

            if (!response.IsAllowed) return Results.Forbid();
        }
        else if (!string.IsNullOrWhiteSpace(message.ConversationId))
        {
            if (!await conversationPermissionService.HasPermission(userId, message.ConversationId)) return Results.Forbid();
        }
        else
        {
            return Results.NotFound();
        }

        var result = await bus.InvokeAsync<PinMessageResponse>(new PinMessageCommand
        {
            MessageId = messageId,
            RequestingUserId = userId,
        });

        if (result.NotFound) return Results.NotFound();
        return Results.Ok(result);
    }

    [WolverineDelete("/api/v1/messaging/{messageId}/pin")]
    public async Task<IResult> UnpinMessage(string messageId, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(message.ChannelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = message.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.PinMessages
                });

            if (!response.IsAllowed) return Results.Forbid();
        }
        else if (!string.IsNullOrWhiteSpace(message.ConversationId))
        {
            if (!await conversationPermissionService.HasPermission(userId, message.ConversationId)) return Results.Forbid();
        }
        else
        {
            return Results.NotFound();
        }

        var result = await bus.InvokeAsync<PinMessageResponse>(new UnpinMessageCommand
        {
            MessageId = messageId,
            RequestingUserId = userId,
        });

        if (result.NotFound) return Results.NotFound();
        return Results.Ok(result);
    }

    [WolverineGet("/api/v1/messaging/pins")]
    public async Task<IResult> GetPinnedMessages([FromQuery] string? channelId, [FromQuery] string? conversationId,
        [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(channelId) && string.IsNullOrWhiteSpace(conversationId)) return Results.BadRequest();

        string contextId;
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = channelId,
                    UserId = userId,
                    Permission = ExternalPermission.ViewChannel
                });

            if (!response.IsAllowed) return Results.Forbid();
            contextId = channelId;
        }
        else
        {
            if (!await conversationPermissionService.HasPermission(userId, conversationId!)) return Results.Forbid();
            contextId = conversationId!;
        }

        var pinned = await repo.GetPinnedMessagesAsync(contextId);
        return Results.Ok(pinned.Select(m => m.ToFacet<Message, MessageDto>()));
    }
}