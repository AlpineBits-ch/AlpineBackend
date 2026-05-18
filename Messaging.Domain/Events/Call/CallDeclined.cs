using Domain;

namespace Messaging.Domain.Events.Call;

public class CallDeclined : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }

}