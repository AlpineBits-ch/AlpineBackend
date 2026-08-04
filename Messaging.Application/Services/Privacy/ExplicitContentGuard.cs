using Domain;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// T2-20. Applies each DM recipient's <see cref="ExplicitContentFilter"/> to the attachments on an
/// outgoing message.
///
/// <para><b>Refuses the send rather than filtering per recipient.</b> A message is one stored row
/// with one attachment list; there is no per-recipient variant of it, so the only enforcement that
/// actually holds is to not store it. In a 1:1 DM that is exactly right. In a group it means one
/// member's filter stops the attachment reaching anyone - heavy-handed, and called out here rather
/// than hidden, because the alternative (store it and hope the client hides it) is the client-side
/// privacy control the spec's second cross-cutting rule forbids.</para>
///
/// <para><b>Classification comes first.</b> With the no-op classifier registered nothing is ever
/// <c>Explicit</c>, so the recipient-policy lookups never run and this costs one virtual call per
/// send. Ordering it the other way round would make every attachment send pay for a filter that
/// cannot fire.</para>
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

    /// <summary>
    /// True when the message must not be stored. False - the answer in every deployment without a
    /// classifier - means send it.
    /// </summary>
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
            // A classifier that fell over does not get to block the product. It reports Unknown by
            // contract; an exception is the same thing said badly.
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
