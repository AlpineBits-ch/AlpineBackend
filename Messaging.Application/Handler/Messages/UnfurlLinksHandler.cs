using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;
using Messaging.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Unfurl.Contracts.Bus;
using Wolverine;
using Wolverine.Attributes;
using MessageEncryptionState = Messaging.Domain.Enums.MessageEncryptionState;
using MessageType = Messaging.Domain.Enums.MessageType;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Resolves the links in a message into previews (docs/specs/message-previews.md).
/// </summary>
[NonTransactional]
public class UnfurlLinksHandler
{
    /// <summary>Resolves the links and attaches the result.</summary>
    public static async Task Handle(
        UnfurlMessageLinks command,
        IMessageRepository repo,
        IMessageBus bus,
        ILogger<UnfurlLinksHandler> logger)
    {
        var message = await repo.GetMessageAsync(command.MessageId);
        if (message is null) return;

        if (MessageFlags.Has(message.Flags, MessageFlags.SuppressEmbeds)) return;

        var urls = LinkExtractor.Extract(message.Content);
        if (urls.Count == 0) return;

        // The hash is taken from the body the links came out of, and travels with the write below.
        var contentHash = ContentHash.Of(message.Content);

        UnfurlUrlsResponse response;
        try
        {
            response = await bus.InvokeAsync<UnfurlUrlsResponse>(new UnfurlUrlsRequest
            {
                Urls = urls,
                CorrelationId = command.MessageId,
            });
        }
        catch (Exception e)
        {
            // Let Wolverine retry: the unfurler being briefly unreachable is transient, and the
            // message is already delivered - the only thing at stake is whether a card shows up.
            logger.LogWarning(e, "Unfurl service did not answer for message {MessageId}", command.MessageId);
            throw;
        }

        var embeds = response.Results
            .Where(r => r.Embed is not null)
            .Select(r => r.Embed!)
            .ToList();

        foreach (var failure in response.Results.Where(r => r.Embed is null))
        {
            logger.LogDebug("No preview for a link in message {MessageId}: {Reason}",
                command.MessageId, failure.FailureReason);
        }

        // Every link failed.
        if (embeds.Count == 0) return;

        var result = await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = command.MessageId,
            RequestingAuthorId = message.AuthorId,
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            ExpectedContentSha256 = contentHash,
            GeneratedEmbedsJson = GeneratedEmbeds.Serialize(embeds),
        });

        if (result.Stale)
        {
            logger.LogDebug("Dropped previews for message {MessageId}: it was edited while they were being fetched",
                command.MessageId);
        }
    }
}
