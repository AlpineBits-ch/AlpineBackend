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
/// Turns links in a freshly-posted message into previews (docs/specs/message-previews.md).
/// </summary>
[NonTransactional]
public class UnfurlLinksHandler
{
    /// <summary>
    /// Decides whether a new message is a candidate at all, and if so hands off to the worker
    /// below.
    /// </summary>
    public static UnfurlMessageLinks? Handle(MessageCreated created)
    {
        // Encrypted contexts: Content is MLS ciphertext, so there is no URL for the server to see.
        if (created.EncryptionState != MessageEncryptionState.Plain) return null;

        // Join/leave/invite system messages have no user-authored body to unfurl.
        if (created.Type != MessageType.Message) return null;

        // A bot or webhook that posted its own cards gets left alone: Discord does not add link
        // previews on top of author-supplied embeds, and there is no sensible way to order the two.
        if (GeneratedEmbeds.HasAuthorEmbeds(created.EmbedsJson)) return null;

        if (LinkExtractor.Extract(created.Content).Count == 0) return null;

        return new UnfurlMessageLinks
        {
            MessageId = created.MessageId,
            ContextId = created.ContextId,
        };
    }

    /// <summary>Re-unfurls after an edit, so changing a link changes the card.</summary>
    public static UnfurlMessageLinks? Handle(MessageUpdated updated)
    {
        if (!updated.IsAuthorEdit) return null;

        if (MessageFlags.Has(updated.Flags, MessageFlags.SuppressEmbeds)) return null;

        if (LinkExtractor.Extract(updated.Content).Count == 0) return null;

        // ContextId is not on this event; either id resolves to the same storage partition, which is
        // all the worker needs.
        var contextId = updated.ConversationId ?? updated.ChannelId;
        if (string.IsNullOrWhiteSpace(contextId)) return null;

        return new UnfurlMessageLinks
        {
            MessageId = updated.MessageId,
            ContextId = contextId,
        };
    }

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
