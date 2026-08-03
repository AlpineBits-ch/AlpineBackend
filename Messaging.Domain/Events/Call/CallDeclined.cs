using Domain;

namespace Messaging.Domain.Events.Call;

public class CallDeclined : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }

    /// <summary>Which of the user's devices turned the call down. Carried for the same reason
    /// <see cref="CallAccepted.DeviceId"/> is: the "stop ringing" push goes to that user's other
    /// devices too, and this is what lets the one that acted recognise and ignore its own copy.</summary>
    public string? DeviceId { get; set; }
}