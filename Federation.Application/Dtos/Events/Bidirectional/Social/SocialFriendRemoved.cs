namespace Federation.Application.Dtos.Events.Bidirectional.Social;

public class SocialFriendRemoved : FederationEvent
{
    public string TargetUserId { get; set; } = string.Empty;
}