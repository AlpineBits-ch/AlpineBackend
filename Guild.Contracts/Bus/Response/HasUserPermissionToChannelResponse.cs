namespace Guild.Contracts.Bus.Response;

public class HasUserPermissionToChannelResponse
{
    public string ChannelId { get; set; }
    public string UserId { get; set; }
    public bool IsAllowed  { get; set; }
    public required ExternalPermission Permission { get; init; }

    /// <summary>The channel's slowmode window in seconds, 0 when off.</summary>
    public int SlowModeSeconds { get; set; }

    /// <summary>
    /// True when this user is exempt from <see cref="SlowModeSeconds"/> (holds ManageChannel or
    /// ManageAnyThread).
    /// </summary>
    public bool CanBypassSlowMode { get; set; }
}