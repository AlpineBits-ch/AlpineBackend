using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class UpdateRoleDto
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string Color { get; set; }
    public Permissions Permissions { get; set; }
}