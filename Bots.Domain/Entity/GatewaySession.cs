namespace Bots.Domain.Entity;

/// <summary>In-memory Gateway WebSocket session state for one connected bot.</summary>
public class GatewaySession
{
    public required string SessionId { get; init; }
    public required string BotUserId { get; init; }
    public long Intents { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
}
