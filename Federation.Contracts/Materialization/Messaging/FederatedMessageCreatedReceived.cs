namespace Federation.Contracts.Materialization.Messaging;

public class FederatedMessageCreatedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required byte[] Content { get; init; }
}

public class FederatedMessageEditedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required byte[] Content { get; init; }
}

public class FederatedMessageDeletedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
}

public class FederatedMessageReactionAddedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required string Emoji { get; init; }
}

public class FederatedMessageReactionRemovedReceived : FederatedResourceReceived
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required string Emoji { get; init; }
}
