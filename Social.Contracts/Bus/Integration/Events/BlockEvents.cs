namespace Social.Contracts.Bus.Integration.Events;

/// <summary>
/// Published when a block is created. Consumers use it to evict their block caches - a block that
/// takes effect on the next cache expiry rather than immediately is the one case where the delay is
/// actually harmful, because the user pressed block to stop something that is happening now.
/// </summary>
public class UserBlockedEvent
{
    public string BlockerId { get; set; } = null!;
    public string BlockedId { get; set; } = null!;
}

/// <summary>Published when a block is lifted. Same cache-eviction purpose as
/// <see cref="UserBlockedEvent"/>.</summary>
public class UserUnblockedEvent
{
    public string BlockerId { get; set; } = null!;
    public string BlockedId { get; set; } = null!;
}
