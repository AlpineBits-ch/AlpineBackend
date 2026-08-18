namespace Federation.Contracts.Materialization.Messaging;

/// <summary>Mirrors Messaging.Domain.Enums.AuthorIdType - Federation.Contracts references no
/// service's domain, so the members are duplicated rather than shared.</summary>
public enum FederatedAuthorIdType
{
    User,
    Bot,
    Webhook,
    Persona,
}
