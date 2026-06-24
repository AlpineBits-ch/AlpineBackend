namespace Federation.Application.Dtos.Events.Bidirectional.Social;

public class SocialFriendRequest : FederationEvent
{
    public string TargetUserId { get; set; } = string.Empty;
}