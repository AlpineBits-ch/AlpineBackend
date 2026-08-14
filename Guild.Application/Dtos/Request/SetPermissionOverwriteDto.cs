using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>
/// The body of the eight <c>PUT
/// /api/v1/{channels|categories}/{id}/permissions/{roles|members}/{id}</c> routes.
/// </summary>
public class SetPermissionOverwriteDto
{
    public Permissions AllowPermissions { get; set; }
    public Permissions DenyPermissions { get; set; }

    /// <inheritdoc cref="SetPermissionOverwriteDto"/>
    public ModulePermissions? AllowModulePermissions { get; set; }

    /// <inheritdoc cref="SetPermissionOverwriteDto"/>
    public ModulePermissions? DenyModulePermissions { get; set; }
}
