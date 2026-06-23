using Federation.Domain.Events;

namespace Federation.Domain.FederationProviders;

public class VentaDomainResolver : IFederatedDomainResolver
{
    public async ValueTask<Uri> ResolveServerUrlAsync(string federatedId, FederationProtocolVersion protocolVersion)
    {
        string targetDomain = ExtractDomainFromId(federatedId);
        
        // maybe some federated domain resolution logic here
        
        return new Uri(targetDomain);
    }
    
    private string ExtractDomainFromId(string id)
    {
        int colonIndex = id.IndexOf(':');
        if (colonIndex == -1 || colonIndex == id.Length - 1)
        {
            throw new ArgumentException($"Identifier '{id}' is missing a valid domain suffix component.");
        }
        return id[(colonIndex + 1)..];
        
    }
}

public class VentaFederationProvider : IFederationProvider
{
    
    public IFederatedDomainResolver DomainResolver { get; }
    public FederationProtocolVersion ProtocolVersion { get; } = new FederationProtocolVersion("venta", 0, 1);
    private readonly HttpClient _httpClient;
    private readonly string federationPath = "/.well-known/federation/events";
    public VentaFederationProvider(IFederatedDomainResolver domainResolver, HttpClient httpClient)
    {
        DomainResolver = domainResolver;
        _httpClient = httpClient;
    }
    
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        // in the future this should start up a outbox, a background worker etc. 
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        // and this should clean it up
        return Task.CompletedTask;
    }

    private HttpClient PrepareHttpClient(Uri domain)
    {
        _httpClient.BaseAddress = domain;
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");
        _httpClient.DefaultRequestHeaders.Add("X-Federated-Protocol", ProtocolVersion.ToString());
        return _httpClient;
    }

    public async Task SendMessageAsync(string channelId, byte[] message, CancellationToken cancellationToken)
    {
        // TODO: implement
        var domain = await DomainResolver.ResolveServerUrlAsync(channelId, ProtocolVersion);
        var httpClient = PrepareHttpClient(domain);
        await httpClient.PutAsync(federationPath, new ByteArrayContent(message), cancellationToken);
    }

    public async Task JoinChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        // TODO: implement
        var domain = await DomainResolver.ResolveServerUrlAsync(channelId, ProtocolVersion);
        var httpClient = PrepareHttpClient(domain);
        await httpClient.PutAsync(federationPath, new StringContent(channelId), cancellationToken);
        
    }

    public async Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        // TODO: implement
        var domain = await DomainResolver.ResolveServerUrlAsync(channelId, ProtocolVersion);
        var httpClient = PrepareHttpClient(domain);
        await httpClient.PutAsync(federationPath, new StringContent(channelId), cancellationToken);
        
    }

    public async Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public event Action<FederatedEvent>? OnFederatedEventReceived;
}