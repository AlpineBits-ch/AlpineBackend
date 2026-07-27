namespace Bots.Application.Dtos.Response;

public class AuthorizeConsentDto
{
    public string ApplicationId { get; set; }
    public string Name { get; set; }
    public string? IconUrl { get; set; }
    public string? Description { get; set; }
    public string GuildId { get; set; }
    public ulong RequestedPermissions { get; set; }

    /// <summary>Requested permissions clamped down to what the installer actually holds.</summary>
    public ulong GrantablePermissions { get; set; }
}
