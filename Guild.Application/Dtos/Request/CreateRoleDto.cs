using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>The body of <c>POST /api/v1/guilds/{guildId}/roles</c>.</summary>
public class CreateRoleDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Color { get; set; } = "#000000";
    public Permissions Permissions { get; set; } = Permissions.None;
    public ModulePermissions ModulePermissions { get; set; } = ModulePermissions.None;
    public bool Hoist { get; set; }
    public bool Mentionable { get; set; } = true;

    /// <summary>Mutually exclusive with <see cref="UnicodeEmoji"/>; passing both is rejected.</summary>
    public string? IconUrl { get; set; }

    /// <inheritdoc cref="IconUrl"/>
    public string? UnicodeEmoji { get; set; }
}
