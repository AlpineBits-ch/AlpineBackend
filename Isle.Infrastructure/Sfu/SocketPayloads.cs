namespace Isle.Infrastructure.Sfu;

public record SubscribeMutualPayload(string TargetPlayerId, string TrackId);
public record UnsubscribeAllPayload(string CellId, IReadOnlyList<string> TrackIds);
public record VoicePositionPayload(string PlayerId, float X, float Y, float Z);