namespace Federation.Contracts.Materialization;

/// <summary>
/// Base for every "a remote instance's event was DAG-resolved and is ready to apply locally"
/// command Federation.Application publishes for the owning service (Guild/Messaging/Social) to
/// materialize as a shadow entity (see the canonical-ID / shadow-entity model in the federation
/// protocol doc).
///
/// <see cref="EventId"/> is the federation protocol's own event id (globally unique per event,
/// already deduplicated once by <c>FederationDagService</c> before this is published) -
/// materialization handlers must still key their own writes on it (upsert, not blind insert)
/// independently, since Wolverine bus delivery is at-least-once.
/// </summary>
public abstract class FederatedResourceReceived
{
    public required string EventId { get; init; }

    /// <summary>
    /// Federation.Application's <c>FederationInstance</c> id for the sending instance. Each
    /// service has its own database (no cross-service FK), so this is stamped on the receiving
    /// service's shadow entity as a plain opaque marker, not a real foreign key.
    /// </summary>
    public required string OriginInstanceId { get; init; }

    /// <summary>Federated id (&lt;localId&gt;:&lt;domain&gt;) of the acting user on the origin instance.</summary>
    public required string SenderId { get; init; }
}
