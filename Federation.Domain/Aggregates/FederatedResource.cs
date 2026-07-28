using Federation.Domain.Events;
using Persistence;

namespace Federation.Domain.Aggregates;

/// <summary>
/// Links one local resource (a Guild, Conversation, or Friendship) to its counterpart on a remote
/// <see cref="FederationInstance"/>.
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
