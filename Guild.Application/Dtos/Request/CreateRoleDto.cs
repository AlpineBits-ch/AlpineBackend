using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateRoleDto
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string Color { get; set; } = "#000000";
    public string GuildId { get; set; }
    public RoleType Type { get; set; } = RoleType.None;
    public Permissions Permissions { get; set; } = Permissions.None;
}