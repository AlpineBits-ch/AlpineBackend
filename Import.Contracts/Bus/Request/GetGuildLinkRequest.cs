namespace Import.Contracts.Bus.Request;

public class GetGuildLinkRequest
{
    public string EchoGuildId { get; set; }
}

public class GetGuildLinkResponse
{
    public string? GuildLinkId { get; set; }
    public string? DiscordGuildId { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
}
