using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;

namespace Messaging.Application.Commands;

public class CreateMessageCommand
{
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;
    public string AuthorId { get; set; }
    public byte[] Content { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string? InReplyTo { get; set; }
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;
    
    public List<string> Mentions { get; set; } = new List<string>();
    public List<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();
}

public class CreateMessageCommandHandler
{
    public async Task<(Message, MessageCreated)> Handle(CreateMessageCommand command, IMessageRepository ctx)
    {
        var message = Message.Create(new CreateMessageParams()
        {
            Content = command.Content,
            ConversationId = command.ConversationId,
            ChannelId = command.ChannelId,
            InReplyTo = command.InReplyTo,
            Mentions = command.Mentions,
            AuthorId = command.AuthorId,
            SenderDeviceId = command.SenderDeviceId,
            EncryptionState = command.EncryptionState,
            MlsEpoch = command.MlsEpoch,
            MlsSequenceNumber = command.MlsSequenceNumber,
            Attachments = command.Attachments
        });
        await ctx.CreateMessageAsync(message);

        return (message, new MessageCreated()
        {
            MessageId = message.Id,
            ChannelId = command.ChannelId,
            ConversationId = command.ConversationId,
            Content = command.Content,
            Mentions = command.Mentions,
            AuthorId = command.AuthorId,
            Attachments = command.Attachments.Select(a => new MinimalAttachment
            {
                Id = a.Id,
                ContentType = a.ContentType,
                FileName = a.FileName,
            }).ToList(),
            EncryptionState = command.EncryptionState,
            MlsEpoch = command.MlsEpoch,
            MlsSequenceNumber = command.MlsSequenceNumber,
            SenderDeviceId = command.SenderDeviceId,
            InReplyTo = command.InReplyTo,
        });
    }
}