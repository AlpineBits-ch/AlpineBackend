namespace Federation.Application.Dtos.Requests;

public record HandshakeRequest(
    string Host,
    string Name,
    string ProtocolVersion,
    byte[] PublicKey,
    byte[] Signature
);
