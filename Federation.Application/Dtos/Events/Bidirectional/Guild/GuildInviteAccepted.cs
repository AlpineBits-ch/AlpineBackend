namespace Federation.Application.Dtos.Events.Bidirectional.Guild;

public class GuildInviteAccepted : FederationEvent
{
    public string GuildId { get; set; } = string.Empty;
    public string InviteCode { get; set; } = string.Empty;
}