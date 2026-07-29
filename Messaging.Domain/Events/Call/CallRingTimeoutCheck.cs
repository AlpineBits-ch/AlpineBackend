namespace Messaging.Domain.Events.Call;

/// <summary>
/// Scheduled follow-up for <see cref="CallCreated"/> - fires once the ring timeout elapses
/// so the call can be auto-declined if nobody has answered yet (e.g. the callee's client
/// never surfaced the call at all, common on mobile when backgrounded/killed).
/// </summary>
public class CallRingTimeoutCheck
{
    public required string CallId { get; set; }
}
