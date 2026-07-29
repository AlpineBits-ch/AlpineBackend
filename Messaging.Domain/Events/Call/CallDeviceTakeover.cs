using Domain;

namespace Messaging.Domain.Events.Call;

/// <summary>A participant accepted the call from a device other than the one they were already
/// connected from. The old device's media session must be torn down.</summary>
public class CallDeviceTakeover : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }
    public string OldDeviceId { get; set; }
    public string NewDeviceId { get; set; }

    /// <summary>The old device's Cloudflare Calls session/track, if it had one - best-effort
    /// server-side cleanup in addition to the old device tearing itself down client-side.</summary>
    public string? OldCfSessionId { get; set; }
    public string? OldAudioTrackName { get; set; }
}
