namespace Federation.Application.Dtos.Events.Bidirectional.Social;

public class SocialFriendRejected : FederationEvent
{
    public string InitiatorUserId { get; set; } = string.Empty;
}