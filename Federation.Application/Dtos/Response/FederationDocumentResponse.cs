using AppEnvironment;

namespace Federation.Application.Dtos.Response;

public class FederationDocumentResponse
{
    public string Instance { get; init; } = Env.GeneralConfiguration.InstanceUrl;
    public string InstanceName { get; init; } = Env.GeneralConfiguration.InstanceName;
    public string Version { get; init; } = Env.GeneralConfiguration.Version;
    public string FederationApi { get; init; } = $"{Env.GeneralConfiguration.InstanceUrl}/federation/v1";
    public FederationDocumentCapabilities Capabilities { get; init; } = new();
}

public class FederationDocumentCapabilities
{
    public bool Guilds { get; init; } = true;
    public bool Users { get; init; } = true;
    public bool Messaging { get; init; } = true;
}