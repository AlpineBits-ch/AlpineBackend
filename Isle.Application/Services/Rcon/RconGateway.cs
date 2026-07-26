using TheIsleEvrimaRconClient;

namespace Isle.Api.Services.Rcon;

/// <inheritdoc cref="IRconGateway"/>
public sealed class RconGateway(EvrimaRconClientConfiguration configuration, ILogger<RconGateway> logger)
    : IRconGateway, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EvrimaRconClient? _client;

    public async Task<T> ExecuteAsync<T>(Func<EvrimaRconClient, Task<T>> operation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            try
            {
                return await operation(client);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The socket most likely died between commands (server restart, idle timeout).
                logger.LogWarning(ex, "RCON command failed; reconnecting and retrying once");
                Drop();
                var reconnected = await ConnectAsync(ct);
                return await operation(reconnected);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ExecuteAsync(Func<EvrimaRconClient, Task> operation, CancellationToken ct = default) =>
        ExecuteAsync<object?>(async client =>
        {
            await operation(client);
            return null;
        }, ct);

    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ConnectAsync(ct);
            return true;
        }
        catch (RconUnavailableException ex)
        {
            logger.LogWarning(ex, "RCON is not reachable");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private async Task<EvrimaRconClient> ConnectAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;

        Drop();
        ct.ThrowIfCancellationRequested();

        var client = new EvrimaRconClient(configuration);
        bool connected;
        try
        {
            connected = await client.ConnectAsync();
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new RconUnavailableException(configuration, ex);
        }

        if (!connected)
        {
            client.Dispose();
            throw new RconUnavailableException(configuration);
        }

        _client = client;
        logger.LogInformation("RCON connected to {Host}:{Port}", configuration.Host, configuration.Port);
        return _client;
    }

    private void Drop()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        Drop();
        _gate.Dispose();
    }
}

public sealed class RconUnavailableException : Exception
{
    public RconUnavailableException(EvrimaRconClientConfiguration configuration, Exception? inner = null)
        : base($"Could not connect to RCON at {configuration.Host}:{configuration.Port}", inner)
    {
    }
}
