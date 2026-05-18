using Domain;

namespace Messaging.Domain.Events.Call;

public class CallEnded : DomainEvent
{
    public string CallId { get; set; }
}