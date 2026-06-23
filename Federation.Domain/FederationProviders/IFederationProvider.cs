using Federation.Domain.Events;

namespace Federation.Domain.FederationProviders;

public record FederationProtocolVersion(string Protocol, int MajorVersion, int MinorVersion)
{
    public override string ToString() => $"{Protocol.ToLower()}/v{MajorVersion}.{MinorVersion}";
}
public interface IFederationProvider
{
    public FederationProtocolVersion ProtocolVersion { get; }

    /// <summary>
    /// Initializes the federation provider, preparing its underlying infrastructure to handle traffic.
    /// </summary>
    /// <remarks>
    /// This method serves as the asynchronous lifecycle entry point for the provider. It should be 
    /// invoked during application startup (typically via an <see cref="Microsoft.Extensions.Hosting.IHostedService"/> 
    /// or background worker) before the system attempts to route outbound traffic or accept inbound events 
    /// through this provider. Typical tasks performed during initialization include allocating network resources, 
    /// loading cryptographic identity keys, or establishing initial connections to seed nodes.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the initialization process.</param>
    /// <returns>A task that represents the asynchronous initialization operation.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown if the provider is already initialized or misconfigured.</exception>
    /// <exception cref="System.OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is triggered before initialization completes.</exception>
    public Task InitializeAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Shuts down the federation provider, gracefully releasing all allocated resources and active connections.
    /// </summary>
    /// <remarks>
    /// This method serves as the asynchronous lifecycle exit point for the provider. It should be 
    /// invoked during application shutdown (typically via an <see cref="Microsoft.Extensions.Hosting.IHostedService"/> 
    /// or background worker) to ensure that outbound queues are drained, remote sockets/webhooks are 
    /// detached, and state files are safely committed to persistent storage before the application fully terminates.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token that can be used to enforce a hard deadline for the shutdown process.</param>
    /// <returns>A task that represents the asynchronous shutdown operation.</returns>
    /// <exception cref="System.OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is triggered before graceful cleanup completes.</exception>
    public Task ShutdownAsync(CancellationToken cancellationToken);

    public Task SendMessageAsync(string channelId, byte[] message, CancellationToken cancellationToken);
    public Task JoinChannelAsync(string channelId, CancellationToken cancellationToken);
    public Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken);
    public Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken);
    
    event Action<FederatedEvent>? OnFederatedEventReceived;
}