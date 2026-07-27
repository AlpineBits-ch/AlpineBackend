using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using MessageEncryptionState = Messaging.Domain.Enums.MessageEncryptionState;

namespace Messaging.Application.Commands;



public class CreateMessageCommandHandler
{
    public async Task<(Message, MessageCreated)> Handle(CreateMessageCommand command, IMessageRepository ctx)
    {

        var encryptionState = MessageEncryptionState.Plain;
        if (command.EncryptionState == Contracts.Bus.Commands.MessageEncryptionState.Encrypted)
        {
            encryptionState = MessageEncryptionState.Encrypted;
        }
        
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
            MlsEpoch = command.MlsEpoch,
            MlsSequenceNumber = command.MlsSequenceNumber,
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
            MlsEpoch = command.MlsEpoch,
            MlsSequenceNumber = command.MlsSequenceNumber,
            SenderDeviceId = command.SenderDeviceId,
            InReplyTo = command.InReplyTo,
        });
    }
}