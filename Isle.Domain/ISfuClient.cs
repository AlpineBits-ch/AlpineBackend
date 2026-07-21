namespace Isle.Api;

// All ids here are the caller's userId (the SignalR user identifier), which is how the
// realtime hub addresses connections. The proximity grid keys players by this same id.
public interface ISfuClient
{
    Task<string?> GetActiveTrackId(string userId);

    Task SubscribeMutual(string userIdA, string userIdB);
    Task UnsubscribeAll(string userId, string cellId);

    /// <summary>Pushes a peer's world position + facing to everyone sharing their voice cell.</summary>
    Task BroadcastPosition(string userId, IReadOnlyList<string> recipients, float x, float y, float z, float yaw);

    /// <summary>Pushes a player their own world position + facing, so the client can place peers relative to itself.</summary>
    Task SendSelfPosition(string userId, float x, float y, float z, float yaw);
}
