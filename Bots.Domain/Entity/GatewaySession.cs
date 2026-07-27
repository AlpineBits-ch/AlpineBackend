namespace Bots.Domain.Entity;

/// <summary>
/// In-memory Gateway WebSocket session state for one connected bot. Deliberately NOT a
/// BaseEntity/persisted row - Resume (OP 6) isn't supported (every attempt gets OP 9 Invalid
/// Session, which every real client library already treats as "reconnect cleanly"), so nothing
/// here needs to survive a disconnect or pod restart.
/// </summary>
public class GatewaySession
{
    public required string SessionId { get; init; }
    public required string BotUserId { get; init; }
    public long Intents { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
}
