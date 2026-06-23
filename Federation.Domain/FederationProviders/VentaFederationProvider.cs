using Federation.Domain.Events;

namespace Federation.Domain.FederationProviders;

public class VentaFederationProvider : IFederationProvider
{
    public FederationProtocolVersion ProtocolVersion { get; } = new FederationProtocolVersion("venta", 0, 1);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string channelId, byte[] message, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task JoinChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public event Action<FederatedEvent>? OnFederatedEventReceived;
}