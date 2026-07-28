namespace Federation.Contracts.Materialization.Conversation;

public class FederatedConversationCreatedReceived : FederatedResourceReceived
{
    public required string ConversationId { get; init; }
    public required string[] MemberIds { get; init; }
}

public class FederatedConversationEditedReceived : FederatedResourceReceived
{
    public required string ConversationId { get; init; }
}

public class FederatedConversationDeletedReceived : FederatedResourceReceived
{
    public required string ConversationId { get; init; }
}

public class FederatedConversationMemberAddedReceived : FederatedResourceReceived
{
    public required string ConversationId { get; init; }
    public required string UserId { get; init; }
}

public class FederatedConversationMemberLeftReceived : FederatedResourceReceived
{
    public required string ConversationId { get; init; }
    public required string UserId { get; init; }
}
