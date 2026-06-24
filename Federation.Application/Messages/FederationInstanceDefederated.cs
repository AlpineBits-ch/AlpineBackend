namespace Federation.Application.Messages;

public record FederationInstanceDefederated(string InstanceId, string Host, string? Reason);
