namespace Isle.Infrastructure.Sfu;

// Wire contracts pushed to the game client.

// A peer became audible.
public record SubscribeMutualPayload(
    string TargetUserId, string Identity, string TrackName, string TrackSid);

// A single peer (by userId) has left your earshot - tear down just their track/spatial node.
public record PeerLeftPayload(string UserId);

// Position of a peer in your voice cell - used to spatialise their audio.
public record VoicePositionPayload(string UserId, float X, float Y, float Z, float Yaw,
    float Vx, float Vy, float Vz, long TimestampMs);

// Your own position + facing - the listener origin for spatialisation.
public record SelfPositionPayload(float X, float Y, float Z, float Yaw,
    float Vx, float Vy, float Vz, long TimestampMs);
