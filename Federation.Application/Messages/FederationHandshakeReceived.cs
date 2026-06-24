using Federation.Domain.Events;

namespace Federation.Application.Messages;

public record FederationHandshakeReceived(
    string Host,
    string Name,
    byte[] PublicKey,
    string ProtocolVersion,
    FederationStatus Status
);
