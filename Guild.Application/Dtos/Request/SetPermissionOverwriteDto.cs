using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class SetPermissionOverwriteDto
{
    public Permissions AllowPermissions { get; set; }
    public Permissions DenyPermissions { get; set; }
}
