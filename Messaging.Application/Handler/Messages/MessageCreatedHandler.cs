using System.Text;
using Echo.Realtime;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;

using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;
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
    public static async Task Handle(MessageCreated messageCreated, IHubContext<EchoRealtimeHub> hubContext, MicroserviceContext ctx, IMessageBus bus,
        BlockCache blocks, PrivacySettingsCache privacySettings, WebPushSender webPush, ILogger<MessageCreatedHandler> logger)
    {
        if (!string.IsNullOrWhiteSpace(messageCreated.ConversationId))
        {
            var allMembers = await ctx.Members.Where(m => m.ConversationId == messageCreated.ConversationId).AsNoTracking().ToListAsync();
            var conversationMembers = allMembers.Where(m => m.UserId != messageCreated.AuthorId).ToList();

            var profile =await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest() { UserId = messageCreated.AuthorId });

            // The author is normally excluded because they already have the message: they sent it,
            // and the send returned it.
            var realtimeRecipients = ServerAuthored(messageCreated.Type)
                ? allMembers
                : conversationMembers;

            if (realtimeRecipients.Count > 0)
            {
                await hubContext.Clients.Users(realtimeRecipients.Select(m => m.UserId))
                    .SendAsync("conversation.MessageCreated", messageCreated);
            }

            // Muted members still receive the realtime push above (their unread badge must stay
            // accurate) - the mute only suppresses the phone notification.
            var now = DateTimeOffset.UtcNow;
            var pushUserIds = conversationMembers.Where(m => !m.IsMuted(now)).Select(m => m.UserId).ToList();

            // A system message is a record of something the user already lived through - a call
            // that just ended on this very device, or one they watched ring out.
            if (messageCreated.Type != DomainMessageType.Message) pushUserIds = [];

            // T0-3's "blocker receives no notification from the blocked user".
            if (pushUserIds.Count > 0)
            {
                var blocked = await blocks.BlockedEitherWayAsync(messageCreated.AuthorId, pushUserIds);
                if (blocked.Count > 0)
                    pushUserIds = pushUserIds.Where(id => !blocked.Contains(id)).ToList();
            }

            if (pushUserIds.Count > 0)
            {
                // WebPush asked for alongside Fcm rather than in a second round trip.
                var response = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
                    new GetPushTokensForUsersRequest
                    {
                        UserIds = pushUserIds,
                        Kinds = [PushTokenKind.Fcm, PushTokenKind.WebPush],
                    });

                // Paired with the user id rather than flattened to a token list: the recipient's own
                // id travels in the payload so the device can find that account's MLS state and
                // decrypt the body itself.
                var recipients = response.Tokens
                    .Where(t => t.Kind == PushTokenKind.Fcm)
                    .Select(t => (t.Token, t.UserId));

                // T2-23. Which of these readers wants routing ids only.
                var settings = await privacySettings.GetAsync(pushUserIds);
                var hideContentFor = settings.Values
                    .Where(s => s.HidePushContent)
                    .Select(s => s.UserId)
                    .ToHashSet(StringComparer.Ordinal);

                var pushPayload = new MessagePushPayload
                {
                    HideContentForUserIds = hideContentFor,
                    MessageId = messageCreated.MessageId,
                    ContextId = messageCreated.ConversationId,
                    ConversationId = messageCreated.ConversationId,
                    AuthorId = messageCreated.AuthorId,
                    SenderName = profile.Profile?.UserName ?? "New message",
                    SenderAvatarUrl = profile.Profile?.AvatarUrl,
                    IsEncrypted = messageCreated.EncryptionState == Domain.Enums.MessageEncryptionState.Encrypted,
                    Content = messageCreated.Content,
                    MlsGeneration = messageCreated.MlsGeneration,
                };

                await MessagePushService.SendAsync(recipients, pushPayload, logger);

                // Browser subscriptions.
                foreach (var subscription in response.Sendable(PushTokenKind.WebPush))
                {
                    await webPush.SendAsync(
                        subscription,
                        MessagePushService.BuildWebPushPayload(
                            pushPayload, subscription.UserId, WebPushEncryption.MaxPayloadBytes),
                        topic: messageCreated.ConversationId);
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
                CreatedAt = messageCreated.CreatedAt,
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

        // Link previews.
        if (UnfurlDecision.ShouldUnfurlNew(
                messageCreated.EncryptionState, messageCreated.Type,
                messageCreated.EmbedsJson, messageCreated.Content))
        {
            await bus.PublishAsync(new UnfurlMessageLinks
            {
                MessageId = messageCreated.MessageId,
                ContextId = messageCreated.ContextId,
            });
        }
    }
    /// <summary>
    /// Whether this message exists because the server wrote it, rather than because its author sent
    /// it.
    /// </summary>
    private static bool ServerAuthored(DomainMessageType type) =>
        type == DomainMessageType.VoiceChannelInvite;

    public static async Task<ICollection<ConversationMember>> LoadAsync(MessageCreated messageCreated, MicroserviceContext ctx)
    {
        return  await ctx.Members.Where(m => m.ConversationId == messageCreated.ConversationId && m.UserId != messageCreated.AuthorId).AsNoTracking().ToListAsync();
    }
}