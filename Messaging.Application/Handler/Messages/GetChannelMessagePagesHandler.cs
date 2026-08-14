using Messaging.Application.Services;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Wolverine;
using DomainEncryptionState = Messaging.Domain.Enums.MessageEncryptionState;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Serves <see cref="GetChannelMessagePagesRequest"/> - the unread previews behind the inbox's
/// Unread tab.
/// </summary>
public class GetChannelMessagePagesHandler
{
    public static async Task<GetChannelMessagePagesResponse> Handle(
        GetChannelMessagePagesRequest request,
        IMessageRepository repo,
        IMessageBus bus,
        ILogger<GetChannelMessagePagesHandler> logger)
    {
        var limit = Math.Clamp(request.MessagesPerChannel, 1, GetChannelMessagePagesRequest.MaxMessagesPerChannel);

        var items = request.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.ChannelId))
            .DistinctBy(i => i.ChannelId, StringComparer.Ordinal)
            .Take(GetChannelMessagePagesRequest.MaxChannels)
            .ToList();

        if (items.Count == 0) return new GetChannelMessagePagesResponse { Pages = [] };

        if (string.IsNullOrWhiteSpace(request.RequestingUserId))
            return new GetChannelMessagePagesResponse { Pages = [] };

        // Two bus round-trips for the whole batch, capped at 25 channels - not two per channel.
        var readable = await MessageHistoryAccess.FilterReadableAsync(
            items.Select(i => i.ChannelId).ToList(), request.RequestingUserId, bus);

        items = items.Where(i => readable.Contains(i.ChannelId)).ToList();

        if (items.Count == 0) return new GetChannelMessagePagesResponse { Pages = [] };

        var pages = await Task.WhenAll(items.Select(item => LoadAsync(item, limit, repo, logger)));

        return new GetChannelMessagePagesResponse { Pages = pages };
    }

    private static async Task<ChannelMessagePage> LoadAsync(
        ChannelMessagePageQuery item, int limit, IMessageRepository repo, ILogger logger)
    {
        try
        {
            ICollection<Message> messages;

            if (string.IsNullOrWhiteSpace(item.AfterMessageId))
            {
                // Never opened: there is no cursor to anchor to, so take the start of the channel.
                (messages, _) = await repo.GetMessagesByChannelIdAsync(item.ChannelId, limit, 0);
            }
            else
            {
                (messages, _) = await repo.GetMessagePageByCursorAsync(new MessagePageQuery
                {
                    ContextId = item.ChannelId,
                    AnchorMessageId = item.AfterMessageId,
                    Direction = MessageCursorDirection.After,
                    Limit = limit,
                });
            }

            return new ChannelMessagePage
            {
                ChannelId = item.ChannelId,
                Messages = messages.Select(Project).ToList(),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load unread previews for channel {ChannelId}", item.ChannelId);
            return new ChannelMessagePage { ChannelId = item.ChannelId, Messages = [] };
        }
    }

    private static InboxMessagePreview Project(Message message) => new()
    {
        Id = message.Id,
        CreatedAt = message.CreatedAt,
        AuthorId = message.AuthorId,
        AuthorDisplayName = message.AuthorDisplayName,
        AuthorAvatarUrl = message.AuthorAvatarUrl,
        Content = message.Content,
        IsEncrypted = message.EncryptionState == DomainEncryptionState.Encrypted,
        MlsGeneration = message.MlsGeneration,
        Type = (int)message.Type,
        SystemMessageVariant = message.SystemMessageVariant,
        EmbedsJson = message.EmbedsJson,
    };
}
