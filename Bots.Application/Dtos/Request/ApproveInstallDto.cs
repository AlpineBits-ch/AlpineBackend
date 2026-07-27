namespace Bots.Application.Dtos.Request;

public class ApproveInstallDto
{
    public string ClientId { get; set; }
    public string GuildId { get; set; }
    public ulong Permissions { get; set; }
    public string? RedirectUri { get; set; }
}
