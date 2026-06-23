namespace Federation.Domain.FederationProviders;

public interface IFederatedDomainResolver
{
    public ValueTask<Uri> ResolveServerUrlAsync(string federatedId, FederationProtocolVersion protocolVersion);
}