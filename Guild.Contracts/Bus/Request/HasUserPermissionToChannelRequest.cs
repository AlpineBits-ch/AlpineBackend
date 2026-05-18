namespace Guild.Contracts.Bus.Request;

public class HasUserPermissionToChannelRequest
{
    public string UserId { get; set; }
    public string ChannelId { get; set; }
    
    public ExternalPermission Permission { get; set; }
    
    public override string ToString()
    {
        return $"HasUserPermissionToChannelRequest(UserId: {UserId}, ChannelId: {ChannelId})";
    }
}