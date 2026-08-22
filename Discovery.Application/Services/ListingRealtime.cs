using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Services;

/// <summary>Every SignalR push this service makes. Nothing else injects IHubContext.</summary>
public class ListingRealtime(IHubContext<EchoRealtimeHub> hub)
{
    /// <summary>
    /// Tells the caller's other devices their interests changed. Carries only the id - the
    /// receiving device refetches, so this can never disagree with the endpoint's own response.
    /// </summary>
    public Task InterestsChangedAsync(string userId, CancellationToken ct) =>
        hub.Clients.User(userId).SendAsync("discovery.InterestsChanged", new { userId }, ct);
}
