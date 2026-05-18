using Domain;

namespace Identity.Contracts.Bus.Events;

public class UserCreatedEvent : DomainEvent
{
    public string UserId { get; set; }
    public string Bio { get; set; }
    public string UserName { get; set; }
}