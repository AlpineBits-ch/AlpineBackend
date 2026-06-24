using Federation.Application.Dtos.Events;

namespace Federation.Application.Messages;

public record FederationInboundEventReady(FederationEvent Event);
