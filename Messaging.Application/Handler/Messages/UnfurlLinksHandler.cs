using AppEnvironment;
using Bots.Contracts.Gateway.Payloads;
using Messaging.Application.Previews;
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

        var instanceHosts = InstanceLinkHosts.All;
        var internalUrls = urls.Where(u => InternalLinkRecognizer.IsInternal(u, instanceHosts)).ToList();
        var externalUrls = urls.Except(internalUrls, StringComparer.OrdinalIgnoreCase).ToList();

        // Keyed by the URL rather than accumulated in resolution order, so the cards below can be
        // put back into the order the links appear in the message.
        var byUrl = new Dictionary<string, EmbedPayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var url in internalUrls)
        {
            // An internal URL whose path is not a shape we know gets no card at all, deliberately:
            // it is still our own host, so the alternative is not "fetch it instead".
            if (!InternalLinkRecognizer.TryRecognize(url, instanceHosts, out var link))
            {
                logger.LogDebug("No preview for an instance link in message {MessageId}: unknown shape",
                    command.MessageId);
                continue;
            }

            var embed = await InternalLinkEmbeds.ResolveAsync(link!, bus);
            if (embed is not null) byUrl[url] = embed;
        }

        if (externalUrls.Count > 0)
        {
            UnfurlUrlsResponse response;
            try
            {
                response = await bus.InvokeAsync<UnfurlUrlsResponse>(new UnfurlUrlsRequest
                {
                    Urls = externalUrls,
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

            foreach (var unfurled in response.Results)
            {
                if (unfurled.Embed is not null) byUrl[unfurled.Url] = unfurled.Embed;
                else
                {
                    logger.LogDebug("No preview for a link in message {MessageId}: {Reason}",
                        command.MessageId, unfurled.FailureReason);
                }
            }
        }

        var embeds = urls
            .Select(u => byUrl.GetValueOrDefault(u))
            .OfType<EmbedPayload>()
            .ToList();

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
