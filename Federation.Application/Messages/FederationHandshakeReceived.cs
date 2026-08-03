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
// Cross-service request/response for the administrator check the federation admin routes are gated
// on (see FederationPolicies.InstanceAdmin).
[JsonSerializable(typeof(Identity.Contracts.Bus.Request.IsUserAdministrativeRequest))]
[JsonSerializable(typeof(Identity.Contracts.Bus.Response.IsUserAdministrativeResponse))]
public partial class FederationMessageContext : JsonSerializerContext
{
}