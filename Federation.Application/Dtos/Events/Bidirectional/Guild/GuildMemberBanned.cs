namespace Federation.Application.Dtos.Events.Bidirectional.Guild;

public class GuildMemberBanned : FederationEvent
{
    public string GuildId { get; set; } = string.Empty;
    public string BannedUserId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
