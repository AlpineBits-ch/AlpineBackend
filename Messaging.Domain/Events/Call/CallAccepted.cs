using Domain;

namespace Messaging.Domain.Events.Call;

public class CallAccepted : DomainEvent
{
    public string CallId { get; set; }
    public string? UserId { get; set; }

}