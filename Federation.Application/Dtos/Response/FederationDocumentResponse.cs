using AppEnvironment;

namespace Federation.Application.Dtos.Response;

public class FederationDocumentResponse
{
    public string Instance { get; init; } = Env.GeneralConfiguration.InstanceUrl;
    public string InstanceName { get; init; } = Env.Federation.InstanceName;
    public string Version { get; init; } = Env.Federation.Version;
    public string FederationApi { get; init; } = $"{Env.GeneralConfiguration.InstanceUrl}/federation/v1";
    public byte[] PublicKey { get; init; } = Env.Federation.PublicKey;
    public FederationDocumentCapabilities Capabilities { get; init; } = new();
}

public class FederationDocumentCapabilities
{
    public bool Guilds { get; init; } = true;
    public bool Users { get; init; } = true;
    public bool Messaging { get; init; } = true;
}