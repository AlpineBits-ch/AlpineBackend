using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Hub = Microsoft.AspNetCore.SignalR.Hub;

namespace Messaging.Application.Hubs;

[Authorize]
public class MessagingHub(ILogger<MessagingHub> logger, MicroserviceContext context, IMessageBus bus) : Hub
{
    public override async Task OnConnectedAsync()
    {
        
        await bus.PublishAsync(new UserActiveEvent() { UserId = Context.UserIdentifier! });
        logger.LogInformation("Client connected, id {ConnectionId}, userId {userId}", Context.ConnectionId, Context.UserIdentifier);
        
        var relationships = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = Context.UserIdentifier! });

        foreach (var relationship in relationships.Profile?.Relationships ?? [])
        {
            await Clients.User(relationship.UserId).SendAsync("UserOnline", Context.UserIdentifier);
        }
        
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client diconnected, id {ConnectionId}, userId {userId}", Context.ConnectionId, Context.UserIdentifier);

        await bus.PublishAsync(new UserInactiveEvent() { UserId = Context.UserIdentifier! });
        
        var relationships = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = Context.UserIdentifier! });

        foreach (var relationship in relationships.Profile?.Relationships ?? [])
        {
            await Clients.User(relationship.UserId).SendAsync("UserOffline", Context.UserIdentifier);
        }
        await base.OnDisconnectedAsync(exception);
    }
    
    [HubMethodName("StartTyping")]

    public async Task StartTyping(string conversationId)
    {
        var userId = Context.UserIdentifier;
        if(userId is null) return;
        var conversation = await context.Conversations.Include(c => c.Members).AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId);

        if(conversation is null) return;

        foreach (var member in conversation.Members)
        {
            await Clients.User(member.UserId).SendAsync("UserTyping", new UserTypingEvent()
            {
                ConversationId = conversationId,
                UserId = userId,
            });
        }
    }

    [HubMethodName("UpdateLastReadMessageByConversation")]
    public async Task UpdateLastReadMessageByConversation(UpdateReadReceiptDto dto)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogError("User is not logged in");
            return;
        }
        logger.LogInformation("Updating last read message {messageId} for conversation {conversationId} by user {userId}", dto.Id, dto.ConversationId, userId);

        var member = await context.Members.Where(c => c.UserId == userId && c.ConversationId == dto.ConversationId).FirstOrDefaultAsync();
        member?.LastReadMessageId = dto.Id;
        await context.SaveChangesAsync();
    }
}   