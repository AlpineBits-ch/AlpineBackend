using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>
/// Answers the <c>FriendsAndServerMembers</c> branch of T0-2: does the initiator share at least one
/// guild with the recipient where the recipient has not turned server DMs off (T2-14)?
/// </summary>
public interface ISharedGuildDirectMessageLookup
{
    /// <summary>
    /// True when the pair share at least one guild the recipient still accepts DMs from.
    /// </summary>
    Task<bool> SharesDirectMessageEnabledGuildAsync(
        string recipientUserId, string initiatorUserId, CancellationToken ct = default);
}

/// <summary>The shipping implementation: intersect first, then ask about consent.</summary>
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
                    // The recipient, always.
                    UserId = recipientUserId,
                    GuildIds = sharedGuildIds,
                }, ct);

            return preferences.Preferences.Any(p => p.AllowDirectMessages);
        }
        catch (Exception e)
        {
            // Fail closed.
            logger.LogWarning(e,
                "Shared-guild DM lookup failed for recipient {RecipientUserId}; treating FriendsAndServerMembers as Friends",
                recipientUserId);
            return false;
        }
    }
}
