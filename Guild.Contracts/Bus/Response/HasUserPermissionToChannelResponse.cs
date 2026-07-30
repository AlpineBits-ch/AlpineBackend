namespace Guild.Contracts.Bus.Response;

public class HasUserPermissionToChannelResponse
{
    public string ChannelId { get; set; }
    public string UserId { get; set; }
    public bool IsAllowed  { get; set; }
    public required ExternalPermission Permission { get; init; }

    /// <summary>The channel's slowmode window in seconds, 0 when off. Rides along on the
    /// permission response rather than being its own request because the only caller that needs
    /// it - Messaging's send path - already makes this exact call on every message, so carrying
    /// it here costs nothing while a separate round trip would double the per-send chatter.</summary>
    public int SlowModeSeconds { get; set; }

    /// <summary>True when this user is exempt from <see cref="SlowModeSeconds"/> (holds
    /// ManageChannel or ManageAnyThread). Resolved here rather than by the caller because
    /// ExternalPermission is deliberately one-question-at-a-time, so asking from Messaging would
    /// cost two more round trips to express a single boolean that Guild can read for free while
    /// it already has the user's resolved permission set in hand.</summary>
    public bool CanBypassSlowMode { get; set; }
}