using Discovery.Contracts.Bus.Events;
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
    /// <summary>Guild's own handler clamps here, so asking for more achieves nothing.</summary>
    private const int FanOutLimit = 500;

    /// <summary>
    /// Tells the caller's other devices their interests changed. Carries only the id - the
    /// receiving device refetches, so this can never disagree with the endpoint's own response.
    /// </summary>
    public Task InterestsChangedAsync(string userId, CancellationToken ct) =>
        hub.Clients.User(userId).SendAsync("discovery.InterestsChanged", new { userId }, ct);

    /// <summary>
    /// Publishes <see cref="ListingStateChanged"/> unconditionally, then fans the SignalR push out
    /// to the guild's members. Discovery holds no membership data and the hub has no guild group -
    /// its one convention is <c>device:{userId}:{deviceId}</c> - so the audience is resolved fresh
    /// from Guild every call.
    /// </summary>
    public async Task ListingChangedAsync(string eventName, Listing listing, CancellationToken ct)
    {
        await bus.PublishAsync(new ListingStateChanged
        {
            ListingId = listing.Id,
            GuildId = listing.GuildId,
            State = listing.State.ToString(),
        });

        var members = await bus.InvokeAsync<ListGuildMembersResponse>(
            new ListGuildMembersRequest { GuildId = listing.GuildId, Limit = FanOutLimit }, ct);

        var audience = members.Members.Where(m => !m.IsBot).Select(m => m.UserId).ToList();
        if (audience.Count == 0) return;

        await hub.Clients.Users(audience).SendAsync(
            eventName,
            new { listingId = listing.Id, guildId = listing.GuildId, state = listing.State.ToString() },
            ct);
    }
}
