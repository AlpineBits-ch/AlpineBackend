namespace Guild.Contracts.Bus.Request;

public class HasUserPermissionToGuildRequest
{
    public string UserId { get; set; }
    public string GuildId { get; set; }

    public ExternalPermission Permission { get; set; }

    public override string ToString()
    {
        return $"HasUserPermissionToGuildRequest(UserId: {UserId}, GuildId: {GuildId})";
    }
}
