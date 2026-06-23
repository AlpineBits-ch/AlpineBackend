namespace Federation.Application.Providers;

public record FederationProtocolVersion(string Protocol, int MajorVersion, int MinorVersion)
{
    public override string ToString() => $"{Protocol.ToLower()}/v{MajorVersion}.{MinorVersion}";
}

public interface IFederatedDomainResolver
{
    ValueTask<Uri> ResolveServerUrlAsync(string federatedId, FederationProtocolVersion protocolVersion);
}
