using System.Text.Json.Serialization;
using Federation.Domain.Events;

namespace Federation.Application.Messages;

public record FederationHandshakeReceived(
    string Host,
    string Name,
    byte[] PublicKey,
    string ProtocolVersion,
    FederationStatus Status
);

[JsonSerializable(typeof(FederationHandshakeReceived))]
[JsonSerializable(typeof(FederationInboundEventReady))]
[JsonSerializable(typeof(FederationInstanceActivated))]
[JsonSerializable(typeof(FederationInstanceBlocked))]
[JsonSerializable(typeof(FederationInstanceDefederated))]
public partial class FederationMessageContext : JsonSerializerContext
{
}