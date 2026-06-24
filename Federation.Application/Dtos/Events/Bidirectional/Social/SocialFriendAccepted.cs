namespace Federation.Application.Dtos.Events.Bidirectional.Social;

public class SocialFriendAccepted : FederationEvent
{
    public string InitiatorUserId { get; set; } = string.Empty;
}