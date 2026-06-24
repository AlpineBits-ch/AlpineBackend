namespace Federation.Application.Dtos.Events.Bidirectional.Guild;

public class GuildMemberLeft : FederationEvent
{
    public string GuildId { get; set; } = string.Empty;
}