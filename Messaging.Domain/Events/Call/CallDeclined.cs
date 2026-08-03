using Domain;

namespace Messaging.Domain.Events.Call;

public class CallDeclined : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }

    /// <summary>Which of the user's devices turned the call down.</summary>
    public string? DeviceId { get; set; }
}