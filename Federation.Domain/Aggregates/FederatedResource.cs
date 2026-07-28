using Federation.Domain.Events;
using Persistence;

namespace Federation.Domain.Aggregates;

/// <summary>
/// Links one local resource (a Guild, Conversation, or Friendship) to its counterpart on a
/// remote <see cref="FederationInstance"/>. Generalized rather than one aggregate per resource
/// type (the original shape was Guild-only, unused, and would have meant near-duplicate
/// FederatedConversation/FederatedFriendship types) since outbound/inbound federation handlers
/// need the same lookup shape for all three: "does this local resource have any linked remote
/// instances" and "which local resource does this remote instance's id map to".
///
/// Per the canonical-ID convention (see the federation protocol doc), <see cref="LocalId"/> and
/// <see cref="RemoteId"/> are the same KSUID-style ids the owning service already uses for the
/// resource on each side - there is no separate id-mapping scheme, only which instance considers
/// itself the origin.
/// </summary>
public class FederatedResource : BaseEntity<FederatedResource>, IPrefixedEntity
{
    public static string Prefix { get; } = "fere";

    public FederatedResourceType ResourceType { get; set; }

    /// <summary>This instance's own id for the resource.</summary>
    public required string LocalId { get; set; }

    /// <summary>The remote instance's id for the same resource.</summary>
    public required string RemoteId { get; set; }

    public required string InstanceId { get; set; }
    public virtual FederationInstance Instance { get; set; } = null!;
}
