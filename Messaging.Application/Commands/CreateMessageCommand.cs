using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using MessageEncryptionState = Messaging.Domain.Enums.MessageEncryptionState;
using DomainMessageType = Messaging.Domain.Enums.MessageType;
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Messaging.Application.Commands;



public class CreateMessageCommandHandler
{
    public async Task<(Message, MessageCreated)> Handle(CreateMessageCommand command, IMessageRepository ctx, MicroserviceContext db)
    {

        var encryptionState = MessageEncryptionState.Plain;
        if (command.EncryptionState == Contracts.Bus.Commands.MessageEncryptionState.Encrypted)
        {
            encryptionState = MessageEncryptionState.Encrypted;
        }

        var type = command.Type switch
        {
            ContractMessageType.Invite => DomainMessageType.Invite,
            ContractMessageType.GuildMemberJoin => DomainMessageType.GuildMemberJoin,
            ContractMessageType.GuildMemberLeave => DomainMessageType.GuildMemberLeave,
            _ => DomainMessageType.Message,
        };

        var message = Message.Create(new CreateMessageParams()
        {
            Content = command.Content,
            ConversationId = command.ConversationId,
            ChannelId = command.ChannelId,
            InReplyTo = command.InReplyTo,
            Mentions = command.Mentions,
            RoleMentions = command.RoleMentions,
            MentionsEveryone = command.MentionsEveryone,
            MentionsHere = command.MentionsHere,
            AuthorId = command.AuthorId,
            SenderDeviceId = command.SenderDeviceId,
            EncryptionState = encryptionState,
            Type = type,
            MlsEpoch = command.MlsEpoch,
            MlsSequenceNumber = command.MlsSequenceNumber,
            EmbedsJson = command.EmbedsJson,
            ComponentsJson = command.ComponentsJson,
            AuthorDisplayName = command.AuthorDisplayName,
            AuthorAvatarUrl = command.AuthorAvatarUrl,
            Attachments = command.Attachments.Select(a => MinimalAttachment.Create(new CreateMinimalAttachmentParams()
            {
                Id = a.Id,
                ThumbnailId = a.ThumbnailId,
                FileName = a.FileName,
                ContentType = a.ContentType,
                ThumbnailUrl = a.ThumbnailUrl,
            })).ToList()
        });
        await ctx.CreateMessageAsync(message);

        // Only Plain-encryption ordinary messages get indexed - there's nothing to search in an
        // MLS-encrypted ciphertext blob server-side, and system messages (join/leave/invite) have
        // no user-authored content worth searching.
        if (encryptionState == MessageEncryptionState.Plain && type == DomainMessageType.Message && message.Content.Length > 0)
        {
            db.MessageSearchEntries.Add(new MessageSearchEntry
            {
                MessageId = message.Id,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                AuthorId = message.AuthorId,
                Content = System.Text.Encoding.UTF8.GetString(message.Content),
                CreatedAt = message.CreatedAt.UtcDateTime,
            });
        }

        return (message, new MessageCreated()
        {
            MessageId = message.Id,
            ChannelId = command.ChannelId,
            ConversationId = command.ConversationId,
            Content = command.Content,
            Mentions = command.Mentions,
            RoleMentions = command.RoleMentions,
            MentionsEveryone = command.MentionsEveryone,
            MentionsHere = command.MentionsHere,
            AuthorId = command.AuthorId,
            Attachments = command.Attachments.Select(a => new MinimalAttachment
            {
                Id = a.Id,
                ContentType = a.ContentType,
                FileName = a.FileName,
            }).ToList(),
            EncryptionState = encryptionState,
            Type = message.Type,
            SystemMessageVariant = message.SystemMessageVariant,
            MlsEpoch = command.MlsEpoch,
            MlsSequenceNumber = command.MlsSequenceNumber,
            // Needed by every consumer that has to decrypt: which of the context's successive MLS
            // groups this ciphertext was sealed to.
            MlsGeneration = command.MlsGeneration,
            SenderDeviceId = command.SenderDeviceId,
            InReplyTo = command.InReplyTo,
            EmbedsJson = command.EmbedsJson,
            ComponentsJson = command.ComponentsJson,
        });
    }
}