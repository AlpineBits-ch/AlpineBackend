namespace Social.Contracts.Bus.Integration.Events;

/// <summary>Published when a block is created.</summary>
public class UserBlockedEvent
{
    public string BlockerId { get; set; } = null!;
    public string BlockedId { get; set; } = null!;
}

/// <summary>Published when a block is lifted.</summary>
public class UserUnblockedEvent
{
    public string BlockerId { get; set; } = null!;
    public string BlockedId { get; set; } = null!;
}
