namespace Guild.Application.Dtos.Request;

public class RolePositionDto
{
    public string RoleId { get; set; }
    public int Position { get; set; }
}

public class ReorderRolesDto
{
    public List<RolePositionDto> Roles { get; set; } = [];
}
