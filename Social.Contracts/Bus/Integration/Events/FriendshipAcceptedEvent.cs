namespace Social.Contracts.Bus.Integration.Events;

public class FriendshipAcceptedEvent
{
    public string AcceptantUserName { get; set; }
    public string InitiatorUserName { get; set; }
    
    public string InitiatorUserId { get; set; }
    public string AcceptantUserId { get; set; }
    public string FriendshipId { get; set; }
}