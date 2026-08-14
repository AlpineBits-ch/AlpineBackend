using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>The body of <c>PATCH /api/v1/roles/{roleId}</c>.</summary>
public class UpdateRoleDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
    public Permissions? Permissions { get; set; }
    public ModulePermissions? ModulePermissions { get; set; }
    public bool? Hoist { get; set; }
    public bool? Mentionable { get; set; }

    /// <summary>Set to a URL to attach an icon, or to an empty string to clear whichever badge the
    /// role currently carries. Mutually exclusive with <see cref="UnicodeEmoji"/>: sending both as
    /// non-empty is rejected rather than resolved, because there is no answer to "which one wins"
    /// worth encoding in two places (see <c>Role.SetBadge</c>).</summary>
    public string? IconUrl { get; set; }

    /// <inheritdoc cref="IconUrl"/>
    public string? UnicodeEmoji { get; set; }
}
