using Domain;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// T2-20. Applies each DM recipient's <see cref="ExplicitContentFilter"/> to the attachments on an
/// outgoing message.
/// </summary>
public sealed class ExplicitContentGuard(
    IMediaClassifier classifier,
    PrivacySettingsCache privacySettings,
    IMessageBus bus,
    ILogger<ExplicitContentGuard> logger)
{
    /// <summary>Refusal code on the send path when a recipient's filter rejects an attachment.
    /// Deliberately does not name which recipient: their filter setting is their business.</summary>
    public const string RefusalCode = "explicit_content_filtered";

    /// <summary>True when the message must not be stored.</summary>
    public async Task<bool> ShouldRefuseAsync(
        string senderUserId,
        IReadOnlyCollection<string> recipientUserIds,
        IReadOnlyCollection<MediaClassificationRequest> attachments,
        CancellationToken ct = default)
    {
        if (attachments.Count == 0 || recipientUserIds.Count == 0) return false;
        if (!classifier.IsOperational) return false;

        IReadOnlyDictionary<string, MediaClassification> verdicts;
        try
        {
            verdicts = await classifier.ClassifyAsync(attachments, ct);
        }
        catch (Exception e)
        {
            // A classifier that fell over does not get to block the product.
            logger.LogWarning(e, "Media classification failed for {Count} attachments", attachments.Count);
            return false;
        }

        var explicitIds = attachments
            .Where(a => verdicts.TryGetValue(a.AttachmentId, out var verdict)
                        && verdict == MediaClassification.Explicit)
            .Select(a => a.AttachmentId)
            .ToList();

        if (explicitIds.Count == 0) return false;

        var recipients = recipientUserIds
            .Where(id => !string.Equals(id, senderUserId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (recipients.Count == 0) return false;

        var settings = await privacySettings.GetAsync(recipients, ct);

        foreach (var recipient in recipients)
        {
            var filter = settings.TryGetValue(recipient, out var record)
                ? record.ExplicitContentFilter
                : ExplicitContentFilter.Everyone;

            var applies = filter switch
            {
                ExplicitContentFilter.Off => false,
                ExplicitContentFilter.Everyone => true,
                ExplicitContentFilter.UnknownSenders => !await IsFriendAsync(recipient, senderUserId, ct),
                _ => true,
            };

            if (applies)
            {
                logger.LogInformation(
                    "Refusing a DM from {SenderUserId} carrying {Count} attachment(s) classified explicit under a recipient's content filter",
                    senderUserId, explicitIds.Count);
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsFriendAsync(string recipientUserId, string senderUserId, CancellationToken ct)
    {
        try
        {
            var response = await bus.InvokeAsync<GetProfileByUserIdResponse>(
                new GetProfileByUserIdRequest { UserId = recipientUserId }, ct);

            return response.Profile is not null && response.Profile.Relationships.Any(r =>
                r.Status == RelationshipStatus.Accepted &&
                string.Equals(r.UserId, senderUserId, StringComparison.Ordinal));
        }
        catch (Exception e)
        {
            // Unknown sender is the restrictive reading of "we could not establish that you are
            // friends", which is what UnknownSenders is about in the first place.
            logger.LogWarning(e, "Could not resolve friendship for the explicit-content filter; treating {SenderUserId} as an unknown sender", senderUserId);
            return false;
        }
    }
}
