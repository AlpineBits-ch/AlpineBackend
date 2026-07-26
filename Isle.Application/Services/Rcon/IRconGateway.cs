using TheIsleEvrimaRconClient;

namespace Isle.Api.Services.Rcon;

/// <summary>
/// Serialised, self-healing access to the game server's RCON socket.
///
/// <para>RCON is a single TCP connection with no request multiplexing, so concurrent callers
/// (population enforcement, the hourly world sweep, kicks) must not write to it at the same time —
/// this gateway is the only holder of the client and runs one command at a time. It also owns the
/// connection lifecycle: the socket is opened on first use rather than at startup, and a command
/// that fails on a dead socket reconnects and retries once instead of failing for good.</para>
/// </summary>
public interface IRconGateway
{
    /// <summary>Runs an RCON command and returns its result.</summary>
    Task<T> ExecuteAsync<T>(Func<EvrimaRconClient, Task<T>> operation, CancellationToken ct = default);

    /// <summary>Runs an RCON command that has no return value.</summary>
    Task ExecuteAsync(Func<EvrimaRconClient, Task> operation, CancellationToken ct = default);

    /// <summary>Opens the socket if it isn't already up. Returns false when the server is unreachable.</summary>
    Task<bool> EnsureConnectedAsync(CancellationToken ct = default);
}
