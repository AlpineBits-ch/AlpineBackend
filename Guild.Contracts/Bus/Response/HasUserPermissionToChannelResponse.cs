namespace Guild.Contracts.Bus.Response;

public class HasUserPermissionToChannelResponse
{
    public string ChannelId { get; set; }
    public string UserId { get; set; }
    public bool IsAllowed  { get; set; }
    public required ExternalPermission Permission { get; init; }
}