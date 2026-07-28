namespace Federation.Contracts.Materialization;

/// <summary>
/// Base for every "a remote instance's event was DAG-resolved and is ready to apply locally"
/// command Federation.Application publishes for the owning service (Guild/Messaging/Social) to
/// materialize as a shadow entity (see the canonical-ID / shadow-entity model in the federation
/// protocol doc).
/// </summary>
public abstract class FederatedResourceReceived
{
    public required string EventId { get; init; }

    /// <summary>
    /// Federation.Application's <c>FederationInstance</c> id for the sending instance.
    /// </summary>
    public required string OriginInstanceId { get; init; }

    /// <summary>Federated id (&lt;localId&gt;:&lt;domain&gt;) of the acting user on the origin instance.</summary>
    public required string SenderId { get; init; }
}
