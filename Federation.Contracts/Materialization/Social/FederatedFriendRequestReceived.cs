namespace Federation.Contracts.Materialization.Social;

public class FederatedFriendRequestReceived : FederatedResourceReceived
{
    public required string TargetUserId { get; init; }
}

public class FederatedFriendAcceptedReceived : FederatedResourceReceived
{
    public required string InitiatorUserId { get; init; }
}

public class FederatedFriendRejectedReceived : FederatedResourceReceived
{
    public required string InitiatorUserId { get; init; }
}

public class FederatedFriendRemovedReceived : FederatedResourceReceived
{
    public required string TargetUserId { get; init; }
}
