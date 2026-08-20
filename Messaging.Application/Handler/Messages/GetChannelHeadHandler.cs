using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Repositories;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Serves <see cref="GetChannelHeadRequest"/> - what Guild rewrites its channel head to after the
/// message it pointed at is deleted.
/// </summary>
public class GetChannelHeadHandler
{
    public static async Task<GetChannelHeadResponse> Handle(
        GetChannelHeadRequest request,
        IMessageRepository repo,
        ILogger<GetChannelHeadHandler> logger)
    {
        if (string.IsNullOrWhiteSpace(request.ChannelId)) return new GetChannelHeadResponse();

        try
        {
            var (messages, _) = await repo.GetMessagesByChannelIdAsync(request.ChannelId, 1, 0);
            var head = messages.LastOrDefault();

            return new GetChannelHeadResponse { MessageId = head?.Id, CreatedAt = head?.CreatedAt };
        }
        catch (Exception ex)
        {
            // An empty answer here would tell Guild the channel is empty and silence it for good,
            // so a failed read has to be a failed request.
            logger.LogWarning(ex, "Could not read the head of channel {ChannelId}", request.ChannelId);
            throw;
        }
    }
}
