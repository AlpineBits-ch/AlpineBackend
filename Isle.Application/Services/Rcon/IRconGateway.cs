using TheIsleEvrimaRconClient;

namespace Isle.Api.Services.Rcon;

/// <summary>Serialised, self-healing access to the game server's RCON socket.</summary>
public interface IRconGateway
{
    /// <summary>Runs an RCON command and returns its result.</summary>
    Task<T> ExecuteAsync<T>(Func<EvrimaRconClient, Task<T>> operation, CancellationToken ct = default);

    /// <summary>Runs an RCON command that has no return value.</summary>
    Task ExecuteAsync(Func<EvrimaRconClient, Task> operation, CancellationToken ct = default);

    /// <summary>Opens the socket if it isn't already up. Returns false when the server is unreachable.</summary>
    Task<bool> EnsureConnectedAsync(CancellationToken ct = default);
}
