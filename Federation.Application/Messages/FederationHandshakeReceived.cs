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
// Cross-service request/response for the administrator check the federation admin routes are
// gated on (see FederationPolicies.InstanceAdmin).
//
// These MUST be listed here. Program.cs installs only source-generated resolvers on Wolverine's
// serializer - EventJsonContext and this context, with no reflection-based fallback - so a message
// type absent from both cannot be serialized at all. The failure is not a compile error and not a
// startup error: the send throws at runtime, the request times out, and the policy fails closed,
// which would 403 every genuine administrator.
[JsonSerializable(typeof(Identity.Contracts.Bus.Request.IsUserAdministrativeRequest))]
[JsonSerializable(typeof(Identity.Contracts.Bus.Response.IsUserAdministrativeResponse))]
public partial class FederationMessageContext : JsonSerializerContext
{
}