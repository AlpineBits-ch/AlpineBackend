namespace Guild.Contracts.Bus.Events;

public class ReactionRemovedEvent
{
    public string MessageId { get; set; }
    public string Emoji { get; set; }
    public string UserId { get; set; }
    
    public string ChannelId { get; set; }
}