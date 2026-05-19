using System.Security.Claims;
using System.Text;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Commands;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

[Authorize]

public class MessagingEndpoints
{
    [WolverinePost("/api/v1/messaging")]
    public async Task<(IResult, MessageCreated?)> CreateMessage(CreateMessageDto dto,  [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext context, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return (Results.Unauthorized(), null);

        
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
            ThumbnailUrl = "https://api.alpinebits.ch/api/v1/messaging/attachments/" + a.Id + "/thumbnail",
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
            Content = Encoding.UTF8.GetBytes(dto.Content),
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
            Attachments = attachments,
            InReplyTo = dto.InReplyTo,
            Mentions = dto.Mentions.ToList(),
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
    public async Task<(IResult, MessageDeleted?)> DeleteMessage(string messageId, [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var message = await ctx.Mapper.FirstOrDefaultAsync<Message>("WHERE message_id = ?", messageId);
        if (message is null) return (Results.NotFound(), null);
        if (message.AuthorId != user.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            return (Results.Forbid(), null);
        }

        await ctx.Mapper.DeleteAsync(message);
        return (Results.Accepted(), new MessageDeleted() { MessageId = messageId });
    }
    
    [WolverinePut("/api/v1/messaging/{messageId}")]
    public async Task<(IResult, MessageDeleted?)> UpdateMessageAsync(string messageId, UpdateMessageDto dto, [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var message = await ctx.Mapper.FirstOrDefaultAsync<Message>("WHERE message_id = ?", messageId);
        if (message is null) return (Results.NotFound(), null);
        if (message.AuthorId != user.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            return (Results.Forbid(), null);
        }

        message.Content = Encoding.UTF8.GetBytes(dto.Content);
        message.UpdatedAt = DateTime.UtcNow;
        await ctx.Mapper.UpdateAsync(message);
        return (Results.Accepted(), new MessageDeleted() { MessageId = messageId });
    }
    
    
    
}