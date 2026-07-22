namespace Isle.Infrastructure.Sfu;

// Wire contracts pushed to the game client. Peers are identified by userId (the SignalR
// user identifier). Yaw is in-game facing in degrees (Unreal convention).

// CfSessionId + TrackName together locate the peer's audio as a Cloudflare "remote"
// track, which the client pulls via tracks/new (location: "remote").
public record SubscribeMutualPayload(string TargetUserId, string CfSessionId, string TrackName);

// A single peer (by userId) has left your earshot — tear down just their track/spatial node.
public record PeerLeftPayload(string UserId);

// Position of a peer in your voice cell — used to spatialise their audio.
// Vx/Vy/Vz: velocity in UE units/second. TimestampMs: server unix-ms the sample was taken.
// Together they let the client extrapolate the peer's position between the ~1 Hz updates.
public record VoicePositionPayload(string UserId, float X, float Y, float Z, float Yaw,
    float Vx, float Vy, float Vz, long TimestampMs);

// Your own position + facing — the listener origin for spatialisation. Carries the same
// velocity + timestamp so your own motion can be extrapolated between updates too.
public record SelfPositionPayload(float X, float Y, float Z, float Yaw,
    float Vx, float Vy, float Vz, long TimestampMs);
