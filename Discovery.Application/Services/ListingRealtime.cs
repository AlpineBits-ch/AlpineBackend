using Discovery.Domain.Entities;
using Echo.Realtime;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Discovery.Api.Services;

/// <summary>Every SignalR push this service makes. Nothing else injects IHubContext.</summary>
public class ListingRealtime(IHubContext<EchoRealtimeHub> hub, IMessageBus bus)
{
    /// <summary>This service's own fan-out budget - Guild's handler clamps at 1000, well above it.</summary>
    private const int FanOutLimit = 500;

    /// <summary>
    /// Tells the caller's other devices their interests changed. Carries only the id - the
    /// receiving device refetches, so this can never disagree with the endpoint's own response.
    /// </summary>
    public Task InterestsChangedAsync(string userId, CancellationToken ct) =>
        hub.Clients.User(userId).SendAsync("discovery.InterestsChanged", new { userId }, ct);

    /// <summary>
    /// Fans a listing change out over SignalR. Discovery holds no membership data and the hub has
    /// no guild group - its one convention is <c>device:{userId}:{deviceId}</c> - so the audience is
    /// resolved fresh from Guild every call.
    /// </summary>
    public async Task ListingChangedAsync(string eventName, Listing listing, CancellationToken ct)
    {
        var audience = await ResolveAudienceAsync(listing.GuildId, ct);
        if (audience.Count == 0) return;

        await hub.Clients.Users(audience).SendAsync(
            eventName,
            new { listingId = listing.Id, guildId = listing.GuildId, state = listing.State.ToString() },
            ct);
    }

    /// <summary>
    /// A separate method rather than widening <see cref="ListingChangedAsync"/>: this is the only
    /// event that carries a reason, and a shared method would put a null reason on every other one.
    /// </summary>
    public async Task ListingSuspendedAsync(Listing listing, CancellationToken ct)
    {
        var audience = await ResolveAudienceAsync(listing.GuildId, ct);
        if (audience.Count == 0) return;

        await hub.Clients.Users(audience).SendAsync(
            "discovery.ListingSuspended",
            new { listingId = listing.Id, guildId = listing.GuildId, reason = WireReason(listing.SuspendedReason) },
            ct);
    }

    private async Task<List<string>> ResolveAudienceAsync(string guildId, CancellationToken ct)
    {
        var members = await bus.InvokeAsync<ListGuildMembersResponse>(
            new ListGuildMembersRequest { GuildId = guildId, Limit = FanOutLimit }, ct);

        return members.Members.Where(m => !m.IsBot).Select(m => m.UserId).ToList();
    }

    // The client maps this to copy and never renders it raw, so it is not the C# enum name.
    private static string WireReason(SuspensionReason? reason) => reason switch
    {
        SuspensionReason.PlanLapsed => "plan_lapsed",
        SuspensionReason.StaffAction => "staff_action",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "A suspended listing always has a reason."),
    };
}
