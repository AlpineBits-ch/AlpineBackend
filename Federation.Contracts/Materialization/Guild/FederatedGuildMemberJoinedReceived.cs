namespace Federation.Contracts.Materialization.Guild;

public class FederatedGuildMemberJoinedReceived : FederatedResourceReceived
{
    public required string GuildId { get; init; }
    public required string UserId { get; init; }
}

public class FederatedGuildMemberLeftReceived : FederatedResourceReceived
{
    public required string GuildId { get; init; }
    public required string UserId { get; init; }
}

public class FederatedGuildMemberBannedReceived : FederatedResourceReceived
{
    public required string GuildId { get; init; }
    public required string BannedUserId { get; init; }
    public string? Reason { get; init; }
}

public class FederatedGuildInviteRedeemedReceived : FederatedResourceReceived
{
    public required string GuildId { get; init; }
    public required string InviteCode { get; init; }
}

public class FederatedGuildJoinRequestReceived : FederatedResourceReceived
{
    public required string GuildId { get; init; }
}
