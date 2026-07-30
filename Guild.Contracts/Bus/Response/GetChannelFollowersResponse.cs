namespace Guild.Contracts.Bus.Response;

public class GetChannelFollowersResponse
{
    public bool IsAnnouncementChannel { get; set; }
    public List<string> TargetChannelIds { get; set; } = [];
}
