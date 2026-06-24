using AppEnvironment;

namespace Federation.Application.Dtos.Response;

public record HandshakeResponse(
    string Host,
    string Name,
    string ProtocolVersion,
    byte[] PublicKey,
    string Status
)
{
    public static HandshakeResponse ForCurrentInstance(string status) => new(
        Env.GeneralConfiguration.InstanceUrl,
        Env.Federation.InstanceName,
        "venta/v0.1",
        Env.Federation.PublicKey,
        status
    );
}
