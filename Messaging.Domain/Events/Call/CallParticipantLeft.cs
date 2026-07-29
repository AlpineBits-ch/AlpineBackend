using Domain;

namespace Messaging.Domain.Events.Call;

/// <summary>A participant left a still-active call (others remain connected). Distinct from
/// <see cref="CallDeclined"/>, which is a pre-connect decline.</summary>
public class CallParticipantLeft : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }
}
