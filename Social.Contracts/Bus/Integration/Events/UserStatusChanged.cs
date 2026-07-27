namespace Social.Contracts.Bus.Integration.Events;

public class UserStatusChanged
{
    public string UserId { get; set; }

    /// <summary>String form of Social.Domain.Enums.OnlineStatus, matching the existing
    /// convention (presence status is passed as a plain string across the realtime/bus
    /// boundary rather than a duplicated contract enum).</summary>
    public string Status { get; set; }
}
