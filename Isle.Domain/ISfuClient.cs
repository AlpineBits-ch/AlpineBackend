namespace Isle.Api;

// All ids here are the caller's userId (the SignalR user identifier), which is how the
// realtime hub addresses connections. The proximity grid keys players by this same id.
public interface ISfuClient
{
    Task<string?> GetActiveTrackId(string userId);

    Task SubscribeMutual(string userIdA, string userIdB);

    /// <summary>Tells both peers to drop each other — one walked out of the other's audible block, or left voice.</summary>
    Task UnsubscribePair(string userIdA, string userIdB);

    /// <summary>Pushes a peer's world position + facing + velocity to everyone within earshot (their 3x3 voice block).</summary>
    Task BroadcastPosition(string userId, IReadOnlyList<string> recipients, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs);

    /// <summary>Pushes a player their own world position + facing + velocity, so the client can place peers relative to itself.</summary>
    Task SendSelfPosition(string userId, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs);

    /// <summary>Sends one peer's current position + velocity to a single recipient — seeds a newly-audible or reconnecting peer.</summary>
    Task SendPeerPosition(string recipientUserId, string peerUserId, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs);
}
