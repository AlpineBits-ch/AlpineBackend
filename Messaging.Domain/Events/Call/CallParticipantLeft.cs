using Domain;

namespace Messaging.Domain.Events.Call;

/// <summary>A participant left a still-active call (others remain connected).</summary>
public class CallParticipantLeft : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }
}
