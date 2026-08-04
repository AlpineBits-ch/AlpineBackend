using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// Answers the <c>FriendsAndServerMembers</c> branch of T0-2: does the initiator share at least one
/// guild with the recipient <i>where the recipient has not turned server DMs off</i> (T2-14)?
///
/// <para>Guild owns both halves of that question - membership and the per-guild
/// <c>GuildDirectMessagePreference</c> - so it can only ever be answered over the bus. It is an
/// interface rather than an inline bus call so the branch has a seam that can be exercised from
/// both sides in tests without a live Guild.</para>
/// </summary>
public interface ISharedGuildDirectMessageLookup
{
    /// <summary>
    /// True when the pair share at least one guild the recipient still accepts DMs from.
    ///
    /// <para>Implementations must fail <b>closed</b>: an unreachable Guild service answers false,
    /// which downgrades <c>FriendsAndServerMembers</c> to <c>Friends</c> for the duration. That is
    /// the restrictive direction, and it is the one the cross-cutting rules in
    /// docs/specs/privacy.md require.</para>
    /// </summary>
    Task<bool> SharesDirectMessageEnabledGuildAsync(
        string recipientUserId, string initiatorUserId, CancellationToken ct = default);
}

/// <summary>
/// The shipping implementation: intersect first, then ask about consent.
///
/// <para><b>Which guilds they share</b> comes from <see cref="GetSharedGuildsRequest"/>, and
/// <b>whether the recipient accepts DMs from those</b> from
/// <see cref="GetGuildDirectMessagePreferenceRequest"/> narrowed to exactly that set. Each returned
/// preference is the <i>effective</i> value - the per-guild override where one exists, otherwise
/// the value derived from the recipient's global policy - so nothing is re-applied on top of
/// it.</para>
///
/// <para><b>That order is a privacy decision, not just a cost one.</b> The preference contract will
/// answer "every guild this user is in" for an empty id list, which would be the cheaper first hop -
/// and would pull a complete guild-membership list for the recipient into Messaging in order to
/// decide a question about one other person. Taking the pairwise intersection first means every
/// guild id that ever reaches this service is one both parties are already in, which is precisely
/// the property <c>GetSharedGuildsRequest</c> is shaped to preserve.</para>
/// </summary>
public sealed class SharedGuildDirectMessageLookup(
    IMessageBus bus,
    ILogger<SharedGuildDirectMessageLookup> logger) : ISharedGuildDirectMessageLookup
{
    public async Task<bool> SharesDirectMessageEnabledGuildAsync(
        string recipientUserId, string initiatorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recipientUserId) || string.IsNullOrWhiteSpace(initiatorUserId))
            return false;

        // A user is never asked to intersect with themselves - that request is ignored by contract,
        // and the answer would be their own complete guild list.
        if (string.Equals(recipientUserId, initiatorUserId, StringComparison.Ordinal)) return false;

        try
        {
            var shared = await bus.InvokeAsync<GetSharedGuildsResponse>(
                new GetSharedGuildsRequest
                {
                    UserId = initiatorUserId,
                    OtherUserIds = [recipientUserId],
                }, ct);

            // A pair with no guild in common is omitted rather than returned empty.
            var sharedGuildIds = shared.Shared
                .Where(s => string.Equals(s.OtherUserId, recipientUserId, StringComparison.Ordinal))
                .SelectMany(s => s.GuildIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (sharedGuildIds.Count == 0) return false;

            var preferences = await bus.InvokeAsync<GetGuildDirectMessagePreferenceResponse>(
                new GetGuildDirectMessagePreferenceRequest
                {
                    // The recipient, always. The initiator's own setting says nothing about who may
                    // reach the recipient.
                    UserId = recipientUserId,
                    GuildIds = sharedGuildIds,
                }, ct);

            return preferences.Preferences.Any(p => p.AllowDirectMessages);
        }
        catch (Exception e)
        {
            // Fail closed. FriendsAndServerMembers reads as Friends until Guild answers again,
            // which refuses a DM that might have been admitted rather than admitting one that
            // should not have been.
            logger.LogWarning(e,
                "Shared-guild DM lookup failed for recipient {RecipientUserId}; treating FriendsAndServerMembers as Friends",
                recipientUserId);
            return false;
        }
    }
}
