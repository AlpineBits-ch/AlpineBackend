namespace Federation.Application.Dtos.Events.Bidirectional.Guild;

public class GuildJoinRequest : FederationEvent
{
    public string GuildId { get; set; } = string.Empty;
}