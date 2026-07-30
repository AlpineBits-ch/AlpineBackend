namespace Guild.Contracts.Bus.Request;

/// <summary>Called by Messaging when a message in an Announcement channel is published - returns
/// every channel (in any guild) currently following ChannelId, so Messaging can fan the message
/// out via its own local CreateMessageCommand for each one.</summary>
public class GetChannelFollowersRequest
{
    public string ChannelId { get; set; } = null!;
}
