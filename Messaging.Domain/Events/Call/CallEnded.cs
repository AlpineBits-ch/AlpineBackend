using Domain;
using Messaging.Domain.Enums;

namespace Messaging.Domain.Events.Call;

public class CallEnded : DomainEvent
{
    public string CallId { get; set; }
    public CallEndReason Reason { get; set; }
}