using Domain;

namespace Messaging.Domain.Events.Call;

public class CallCreated : DomainEvent
{
    public string CallId { get; set; }
}