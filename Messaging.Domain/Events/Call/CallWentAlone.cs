using Domain;

namespace Messaging.Domain.Events.Call;

/// <summary>A Leave dropped the call to exactly one connected participant. Starts the grace
/// period after which the call is auto-ended if nobody rejoins.</summary>
public class CallWentAlone : DomainEvent
{
    public string CallId { get; set; }
    public string UserId { get; set; }
    public DateTime AloneSince { get; set; }
}
