namespace Bots.Application.Dtos.Request;

public class CreateBotApplicationDto
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public ulong DefaultPermissions { get; set; }
}
