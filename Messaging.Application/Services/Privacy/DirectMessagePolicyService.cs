using Domain;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// The machine-readable half of a refusal. <see cref="UserId"/> is the recipient the decision was
/// taken about - always someone the caller already named, so it is not an enumeration oracle.
/// </summary>
public sealed record DmRefusal(string Code, string? UserId)
{
    /// <summary>The caller blocked this person. Only ever returned to the <i>blocker</i>: they took
    /// the action, so naming it is not a leak, and "unblock them to message them" is the only
    /// useful thing a client can say here.</summary>
    public const string Blocked = "blocked";

    /// <summary>The recipient does not accept this DM. Covers a policy refusal <b>and</b> the case
    /// where the recipient has blocked the caller - deliberately the same code, because T0-3 says a
    /// block must not be distinguishable by the person who was blocked from ordinary
    /// unfriendliness.</summary>
    public const string RecipientPolicy = "recipient_dm_policy";

    /// <summary>Neither the cache nor the owning service could answer. Not a policy decision and
    /// not reported as one - the request is refused, but the client is told to retry rather than
    /// shown a permission error the user cannot act on.</summary>
    public const string LookupUnavailable = "privacy_lookup_unavailable";

    public bool IsTransient => Code == LookupUnavailable;
}

/// <summary>
/// T0-2. Decides whether one account may open a conversation with, add, call or newly message
/// another.
///
/// <para><b>The direction is the fix.</b> Both sites this replaces
/// (<c>ConversationEndpoints.CreateConversation</c> and the friendship helper behind
/// <c>/consume-tokens</c> and <c>AddConversationMember</c>) asked whether the <i>initiator</i>
/// counted the recipient as a friend. That is the wrong party's setting: the control belongs to the
/// person being contacted. Everything below reads the recipient's record and the recipient's
/// relationship list.</para>
///
/// <para>Order of resolution, per recipient:</para>
/// <list type="number">
///   <item>a block, in either direction, refuses before policy is consulted at all;</item>
///   <item><c>Everyone</c> admits; <c>Nobody</c> refuses;</item>
///   <item><c>Friends</c> admits only an accepted relationship;</item>
///   <item><c>FriendsAndServerMembers</c> admits a friend, or a shared guild the recipient still
///   accepts DMs from (see <see cref="ISharedGuildDirectMessageLookup"/>).</item>
/// </list>
///
/// <para>Existing conversations are not re-evaluated for membership - a policy change governs new
/// conversations and new one-to-one sends, never who is already in a group.</para>
/// </summary>
public sealed class DirectMessagePolicyService(
    PrivacySettingsCache privacySettings,
    BlockCache blocks,
    ISharedGuildDirectMessageLookup sharedGuilds,
    IMessageBus bus,
    ILogger<DirectMessagePolicyService> logger)
{
    /// <summary>
    /// The first refusal among <paramref name="recipientUserIds"/>, or null when every one of them
    /// admits the initiator.
    ///
    /// <para><paramref name="knownRecipientProfiles"/> lets a caller that already fetched profiles
    /// (conversation creation does) hand them in instead of paying for a second round trip. Any
    /// recipient missing from it is resolved on demand, and only when the policy in force actually
    /// needs a friendship answer - a recipient on <c>Everyone</c> costs no profile lookup at
    /// all.</para>
    /// </summary>
    public async Task<DmRefusal?> EvaluateAsync(
        string initiatorUserId,
        IReadOnlyCollection<string> recipientUserIds,
        IReadOnlyDictionary<string, ProfileDto>? knownRecipientProfiles = null,
        CancellationToken ct = default)
    {
        var targets = recipientUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, initiatorUserId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (targets.Count == 0) return null;

        // ── 1. Blocking, before policy ────────────────────────────────────────
        var relations = await blocks.GetAsync(initiatorUserId, ct);
        if (relations.Degraded)
            return new DmRefusal(DmRefusal.LookupUnavailable, null);

        foreach (var target in targets)
        {
            // The caller's own action - naming it is what lets a client offer "unblock".
            if (relations.Blocking.Contains(target))
                return new DmRefusal(DmRefusal.Blocked, target);

            // Someone else's action, and one the caller must not be able to detect. Reported with
            // exactly the code a Friends-only recipient produces.
            if (relations.BlockedBy.Contains(target))
                return new DmRefusal(DmRefusal.RecipientPolicy, target);
        }

        // ── 2. Each recipient's own policy ────────────────────────────────────
        var settings = await privacySettings.GetAsync(targets, ct);

        foreach (var target in targets)
        {
            var policy = settings.TryGetValue(target, out var record)
                ? record.DirectMessagePolicy
                // GetAsync always fills every id, so this is unreachable in practice - and if it
                // ever is reached, Friends is the restrictive answer, not Everyone.
                : DirectMessagePolicy.Friends;

            var admitted = policy switch
            {
                DirectMessagePolicy.Everyone => true,
                DirectMessagePolicy.Nobody => false,
                DirectMessagePolicy.Friends =>
                    await IsFriendOfAsync(target, initiatorUserId, knownRecipientProfiles, ct),
                DirectMessagePolicy.FriendsAndServerMembers =>
                    await IsFriendOfAsync(target, initiatorUserId, knownRecipientProfiles, ct)
                    || await sharedGuilds.SharesDirectMessageEnabledGuildAsync(target, initiatorUserId, ct),
                // A value this build does not know about is not an excuse to admit.
                _ => false,
            };

            if (!admitted) return new DmRefusal(DmRefusal.RecipientPolicy, target);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="initiatorUserId"/> is an accepted friend <i>of the recipient</i>.
    ///
    /// <para>Read off the recipient's relationship list rather than the initiator's. Relationships
    /// are symmetric in Social today, so both sides agree - but the setting being enforced is the
    /// recipient's, and reading their record is what keeps that true if the two ever diverge.</para>
    ///
    /// <para>A profile that cannot be fetched is not a friend. Failing closed here is what stops a
    /// Social outage from turning a friends-only account into an open one.</para>
    /// </summary>
    private async Task<bool> IsFriendOfAsync(
        string recipientUserId,
        string initiatorUserId,
        IReadOnlyDictionary<string, ProfileDto>? known,
        CancellationToken ct)
    {
        if (known is not null && known.TryGetValue(recipientUserId, out var supplied))
            return HasAcceptedRelationship(supplied, initiatorUserId);

        try
        {
            var response = await bus.InvokeAsync<GetProfileByUserIdResponse>(
                new GetProfileByUserIdRequest { UserId = recipientUserId }, ct);

            return response.Profile is not null && HasAcceptedRelationship(response.Profile, initiatorUserId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not resolve the friendship between {RecipientUserId} and {InitiatorUserId}; refusing",
                recipientUserId, initiatorUserId);
            return false;
        }
    }

    private static bool HasAcceptedRelationship(ProfileDto profile, string otherUserId) =>
        profile.Relationships.Any(r =>
            r.Status == RelationshipStatus.Accepted &&
            string.Equals(r.UserId, otherUserId, StringComparison.Ordinal));
}
