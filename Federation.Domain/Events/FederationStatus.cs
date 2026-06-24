namespace Federation.Domain.Events;

public enum FederationStatus
{
    Pending,
    Active,
    Suspended,
    Defederated,
    Blocked
}