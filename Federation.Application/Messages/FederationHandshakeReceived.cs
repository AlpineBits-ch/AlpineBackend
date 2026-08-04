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
// The warning that used to sit here - that any bus message type absent from this context could not
// be serialized at all - was accurate, and the trap it described went off. ExportUserDataCommand
// and PurgeUserDataCommand were both added to Federation's handlers without being added here, so
// Federation silently answered neither: data exports resolved Partial naming it, and account
// deletions hung outright. Nothing surfaced an error, because the envelope died in deserialization
// before reaching a handler.
//
// Program.cs no longer installs these contexts as Wolverine's whole resolver chain, so that is no
// longer possible: bus messages resolve reflectively, like every other service. This context is
// retained for the HTTP surface, where ASP.NET keeps a reflection resolver in the chain and adding
// a source-generated context only prioritises it.
[JsonSerializable(typeof(Identity.Contracts.Bus.Request.IsUserAdministrativeRequest))]
[JsonSerializable(typeof(Identity.Contracts.Bus.Response.IsUserAdministrativeResponse))]
public partial class FederationMessageContext : JsonSerializerContext
{
}