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

                // Paired with the user id rather than flattened to a token list: the recipient's own
                // id travels in the payload so the device can find that account's MLS state and
                // decrypt the body itself.
                var recipients = response.Tokens
                    .Where(t => t.Kind == PushTokenKind.Fcm)
                    .Select(t => (t.Token, t.UserId));

                await MessagePushService.SendAsync(recipients, new MessagePushPayload
                {
                    MessageId = messageCreated.MessageId,
                    ContextId = messageCreated.ConversationId,
                    ConversationId = messageCreated.ConversationId,
                    AuthorId = messageCreated.AuthorId,
                    SenderName = profile.Profile?.UserName ?? "New message",
                    SenderAvatarUrl = profile.Profile?.AvatarUrl,
                    IsEncrypted = messageCreated.EncryptionState == Domain.Enums.MessageEncryptionState.Encrypted,
                    Content = messageCreated.Content,
                    MlsGeneration = messageCreated.MlsGeneration,
                }, logger);
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
                // Was hardcoded to Plain, which told Guild - and through it every realtime client
                // and the channel push path - that an MLS-encrypted channel message was readable
                // text. Clients then rendered ciphertext instead of decrypting it.
                EncryptionState = messageCreated.EncryptionState == Domain.Enums.MessageEncryptionState.Encrypted
                    ? MessageEncryptionState.Encrypted
                    : MessageEncryptionState.Plain,
                MlsGeneration = messageCreated.MlsGeneration,
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