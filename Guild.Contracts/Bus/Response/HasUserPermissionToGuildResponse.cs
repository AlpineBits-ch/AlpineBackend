namespace Guild.Contracts.Bus.Response;

public class HasUserPermissionToGuildResponse
{
    public string GuildId { get; set; }
    public string UserId { get; set; }
    public bool IsAllowed { get; set; }
    public required ExternalPermission Permission { get; init; }
}
