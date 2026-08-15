using Microsoft.AspNetCore.SignalR;

namespace Echo.Realtime;

/// <summary>
/// How long a <see cref="EchoRealtimeHub"/> connection is allowed to go quiet before the server
/// declares it dead.
/// </summary>
public static class RealtimeHubTimeouts
{
    /// <summary>How often the server pings an idle connection.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the server waits for anything at all from a client before closing the connection.
    /// </summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Applies both to a <see cref="HubOptions"/>. Passed straight to <c>AddSignalR</c>.</summary>
    public static void Configure(HubOptions options)
    {
        options.KeepAliveInterval = KeepAlive;
        options.ClientTimeoutInterval = ClientTimeout;
    }
}
