namespace Isle.Infrastructure.Sfu;

// Wire contracts pushed to the game client.

// CfSessionId + TrackName together locate the peer's audio as a Cloudflare "remote"
// track, which the client pulls via tracks/new (location: "remote").
public record SubscribeMutualPayload(string TargetUserId, string CfSessionId, string TrackName);

public record UnsubscribeAllPayload(string CellId, IReadOnlyList<string> TrackIds);

// Position of a peer in your voice cell — used to spatialise their audio.
public record VoicePositionPayload(string UserId, float X, float Y, float Z, float Yaw);

// Your own position + facing — the listener origin for spatialisation.
public record SelfPositionPayload(float X, float Y, float Z, float Yaw);
