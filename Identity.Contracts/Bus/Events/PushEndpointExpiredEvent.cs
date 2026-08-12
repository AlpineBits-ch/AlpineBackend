using Identity.Contracts.Enums;

namespace Identity.Contracts.Bus.Events;

/// <summary>A push service told us one of our endpoints no longer exists.</summary>
public class PushEndpointExpiredEvent
{
    /// <summary>Which transport reported it.</summary>
    public PushTokenKind Kind { get; set; }

    /// <summary>The addressable value - a Web Push endpoint URL, or an FCM/APNs token.</summary>
    public string Token { get; set; } = null!;
}
