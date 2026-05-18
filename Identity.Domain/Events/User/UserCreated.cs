using Domain;

namespace Identity.Domain.Events.User;

public class UserCreated : DomainEvent
{
    public string UserId { get; set; }
    public string Email { get; set; }
    public string UserName { get; set; }
    public string? Bio { get; set; }

 
}