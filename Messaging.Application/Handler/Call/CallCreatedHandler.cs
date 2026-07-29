using Messaging.Domain.Events.Call;
using Wolverine;

namespace Messaging.Application.Handler.Call;

public class CallCreatedHandler
{
    /// <summary>How long a callee has to answer before the call is auto-declined.</summary>
    private static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(50);

    public static object Handle(CallCreated @event)
    {
        return new CallRingTimeoutCheck { CallId = @event.CallId }.DelayedFor(RingTimeout);
    }
}
