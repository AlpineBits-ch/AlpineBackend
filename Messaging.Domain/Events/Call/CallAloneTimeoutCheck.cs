namespace Messaging.Domain.Events.Call;

/// <summary>
/// Scheduled follow-up for <see cref="CallWentAlone"/> - fires once the alone grace period elapses
/// so the sole remaining participant can be disconnected if nobody rejoined.
/// </summary>
public class CallAloneTimeoutCheck
{
    public required string CallId { get; set; }

    /// <summary>The Call.AloneSince value at scheduling time - if it no longer matches when this
    /// fires, someone rejoined and then the call went alone again (or ended), so this stale
    /// check is a no-op.</summary>
    public required DateTime ExpectedAloneSince { get; set; }
}
