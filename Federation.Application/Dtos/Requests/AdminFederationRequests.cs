using Federation.Domain.Aggregates;

namespace Federation.Application.Dtos.Requests;

public record InitiateHandshakeRequest(string TargetHost);

public record DefederateRequest(string? Reason);

public record UpdateFederationSettingsRequest(AcceptancePolicy AcceptancePolicy);
