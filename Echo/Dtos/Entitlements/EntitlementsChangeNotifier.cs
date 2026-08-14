using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Echo.Dtos.Entitlements;

/// <summary>Pushes <c>entitlements.Changed</c> when a subject's entitlements move.</summary>
public sealed class EntitlementsChangeNotifier(
    IHubContext<EchoRealtimeHub> hub,
    ILogger<EntitlementsChangeNotifier> logger)
{
    /// <param name="version">The same counter the snapshot reports.</param>
    /// <param name="changedKeys">Advisory.</param>
    public Task NotifyUserAsync(
        string userId,
        long version,
        IReadOnlyList<string>? changedKeys = null,
        CancellationToken cancellationToken = default)
    {
        var subject = EntitlementSubject.ForUser(userId);

        return hub.Clients.User(userId).SendAsync(
            EntitlementRealtimeEvents.Changed,
            EntitlementsChangedDto.For(subject, version, changedKeys),
            cancellationToken);
    }

    /// <param name="memberUserIds">Everyone who should hear about it, which is the guild's members.
    /// Nothing is sent for an empty list.</param>
    public Task NotifyGuildAsync(
        string guildId,
        IReadOnlyList<string> memberUserIds,
        long version,
        IReadOnlyList<string>? changedKeys = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memberUserIds);

        if (memberUserIds.Count == 0)
        {
            logger.LogDebug(
                "Entitlements changed for guild {GuildId} with no recipients, so nothing was pushed", guildId);
            return Task.CompletedTask;
        }

        var subject = EntitlementSubject.ForGuild(guildId);

        return hub.Clients.Users(memberUserIds).SendAsync(
            EntitlementRealtimeEvents.Changed,
            EntitlementsChangedDto.For(subject, version, changedKeys),
            cancellationToken);
    }
}
