using Domain;

namespace Messaging.Domain.Events.Call;

/// <summary>A decline arrived from a device after the same user had already connected to the
/// call from a different device. The call is unaffected - only the stale device needs telling
/// to stop ringing.</summary>
public class CallDeviceDismissed : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }
    public string DeviceId { get; set; }
}
