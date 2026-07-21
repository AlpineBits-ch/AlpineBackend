namespace Isle.Infrastructure.Sfu;

// CfSessionId + TrackName together locate the peer's audio as a Cloudflare "remote"
// track, which the client pulls via tracks/new (location: "remote").
public record SubscribeMutualPayload(string TargetPlayerId, string CfSessionId, string TrackName);
public record UnsubscribeAllPayload(string CellId, IReadOnlyList<string> TrackIds);
public record VoicePositionPayload(string PlayerId, float X, float Y, float Z);