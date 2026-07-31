using System.Text;
using Echo.Realtime;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;

using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Attributes;
using Message = FirebaseAdmin.Messaging.Message;
using DomainMessageType = Messaging.Domain.Enums.MessageType;
using ChannelMessageType = Guild.Contracts.Bus.Events.MessageType;

namespace Messaging.Application.Handler.Messages;

[NonTransactional]
public class MessageCreatedHandler
{
    public static async Task Handle(MessageCreated messageCreated, IHubContext<EchoRealtimeHub> hubContext, MicroserviceContext ctx, IMessageBus bus, ILogger<MessageCreatedHandler> logger)
    {
        if (!string.IsNullOrWhiteSpace(messageCreated.ConversationId))
        {
            var conversationMembers = await ctx.Members.Where(m => m.ConversationId == messageCreated.ConversationId && m.UserId != messageCreated.AuthorId).AsNoTracking().ToListAsync();

            var profile =await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = messageCreated.AuthorId });
            await hubContext.Clients.Users(conversationMembers.Select(m => m.UserId)).SendAsync("conversation.MessageCreated", messageCreated);

            // Muted members still receive the realtime push above (their unread badge must stay
            // accurate) - the mute only suppresses the phone notification.
            var now = DateTimeOffset.UtcNow;
            var pushUserIds = conversationMembers.Where(m => !m.IsMuted(now)).Select(m => m.UserId).ToList();

            if (pushUserIds.Count > 0)
            {
                var response = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
                    new GetPushTokensForUsersRequest { UserIds = pushUserIds, Kinds = [PushTokenKind.Fcm] });
                string body = Encoding.UTF8.GetString(messageCreated.Content);
                if (messageCreated.EncryptionState == Domain.Enums.MessageEncryptionState.Encrypted)
                {
                    body = "You have a new encrypted message";
                }
                foreach (var token in response.Of(PushTokenKind.Fcm))
                {
                    await PushNotifiaction.SendPushNotification(new PushNotificationParams()
                    {
                        Token = token,
                        Title = profile.Profile?.UserName ?? "New message",
                        Body = body,
                        Data = new Dictionary<string, string> { ["conversationId"] = messageCreated.ConversationId },
                    });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(messageCreated.ChannelId))
        {
            await bus.SendAsync(new MessageCreatedForChannel()
            {
                AuthorId = messageCreated.AuthorId,
                ChannelId = messageCreated.ChannelId,
                MessageId = messageCreated.MessageId,
                Content = messageCreated.Content,
                Mentions = messageCreated.Mentions,
                RoleMentions = messageCreated.RoleMentions,
                MentionsEveryone = messageCreated.MentionsEveryone,
                MentionsHere = messageCreated.MentionsHere,
                EncryptionState = MessageEncryptionState.Plain,
                EmbedsJson = messageCreated.EmbedsJson,
                ComponentsJson = messageCreated.ComponentsJson,
                Type = messageCreated.Type switch
                {
                    DomainMessageType.Invite => ChannelMessageType.Invite,
                    DomainMessageType.GuildMemberJoin => ChannelMessageType.GuildMemberJoin,
                    DomainMessageType.GuildMemberLeave => ChannelMessageType.GuildMemberLeave,
                    _ => ChannelMessageType.Message,
                },
                SystemMessageVariant = messageCreated.SystemMessageVariant,
                Attachments = messageCreated.Attachments.Select(a => new MinimalAttachmentForChannel()
                {
                    Id = a.Id,
                    CreatedAt = a.CreatedAt,
                    UpdatedAt = a.UpdatedAt,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    ThumbnailUrl = a.ThumbnailUrl,
                    ThumbnailId = a.ThumbnailId,
                }).ToList()
            });
        }
    }
    public static async Task<ICollection<ConversationMember>> LoadAsync(MessageCreated messageCreated, MicroserviceContext ctx)
    {
        return  await ctx.Members.Where(m => m.ConversationId == messageCreated.ConversationId && m.UserId != messageCreated.AuthorId).AsNoTracking().ToListAsync();
    }
}