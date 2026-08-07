namespace Identity.Contracts;

/// <summary>
/// Capability strings about what a client build can do with a push notification, reported at device
/// registration alongside <c>Domain.MlsCapabilities</c> and handed to senders on <see
/// cref="Bus.Response.PushTokenResponse.Capabilities"/>.
/// </summary>
public static class PushCapabilities
{
    /// <summary>
    /// Renders notifications from a localization key and arguments rather than from the server's
    /// English text.
    /// </summary>
    public const string LocalizedV1 = "push.loc.v1";
}
