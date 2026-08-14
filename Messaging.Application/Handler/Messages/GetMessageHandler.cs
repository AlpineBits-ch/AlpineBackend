using Messaging.Application.Services;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Wolverine;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Serves <see cref="GetMessageRequest"/> - the read Bots needs to validate a component interaction
/// against the message that supposedly carried the component, plus the inbox's mention bodies and
/// the read cursor's timestamp lookup.
/// </summary>
public class GetMessageHandler
{
    public static async Task<GetMessageResponse> Handle(
        GetMessageRequest request,
        IMessageRepository repo,
        ConversationPermissionService conversations,
        IMessageBus bus,
        ILogger<GetMessageHandler> logger)
    {
        var message = await repo.GetMessageAsync(request.MessageId);
        if (message is null) return new GetMessageResponse();

        if (!await MayReadAsync(message, request, conversations, bus))
        {
            logger.LogWarning(
                "Refused GetMessageRequest for {MessageId} by {UserId} at scope {Scope}",
                request.MessageId, request.RequestingUserId, request.Scope);

            // Same shape as a miss.
            return new GetMessageResponse();
        }

        var metadataOnly = request.Scope == MessageReadScope.MetadataOnly;

        return new GetMessageResponse
        {
            Message = new MessageSummary
            {
                Id = message.Id,
                CreatedAt = message.CreatedAt,
                AuthorId = message.AuthorId,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                Content = metadataOnly ? [] : message.Content,
                ComponentsJson = metadataOnly ? null : message.ComponentsJson,
                EmbedsJson = metadataOnly ? null : message.EmbedsJson,
            },
        };
    }

    private static async Task<bool> MayReadAsync(
        Message message, GetMessageRequest request, ConversationPermissionService conversations, IMessageBus bus)
    {
        if (string.IsNullOrWhiteSpace(request.RequestingUserId)) return false;

        if (!string.IsNullOrWhiteSpace(message.ChannelId))
        {
            return request.Scope == MessageReadScope.MetadataOnly
                ? await MessageHistoryAccess.MayViewAsync(message.ChannelId, request.RequestingUserId, bus)
                : await MessageHistoryAccess.MayReadAsync(message.ChannelId, request.RequestingUserId, bus);
        }

        // A DM has no roles and no overwrites, so membership is the whole question and the scope
        // does not change it - there is no narrower bit to fall back to.
        if (!string.IsNullOrWhiteSpace(message.ConversationId))
            return await conversations.HasPermission(request.RequestingUserId, message.ConversationId);

        // Neither a channel nor a conversation means there is nothing to resolve an answer against.
        return false;
    }
}
