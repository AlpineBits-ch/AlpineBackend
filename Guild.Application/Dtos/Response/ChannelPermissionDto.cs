using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// Category and Member are excluded rather than nested: an overwrite is polymorphic, so at most one
/// of them is ever set, and CategoryId/MemberId already say which.
/// </summary>
[Facet(typeof(ChannelPermission),
    nameof(ChannelPermission.Category), nameof(ChannelPermission.Member),
    NestedFacets = [typeof(ChannelDto), typeof(ChannelPermissionDto), typeof(GuildDto), typeof(RoleDto)])]
public partial class ChannelPermissionDto
{

}

/// <summary>
/// An overwrite reduced to its scoping ids and its allow/deny masks - no Channel, Category, Role or
/// Guild object hanging off it.
/// </summary>
[Facet(typeof(ChannelPermission),
    Include =
    [
        "Id", "CreatedAt", "UpdatedAt",
        nameof(ChannelPermission.ChannelId), nameof(ChannelPermission.CategoryId),
        nameof(ChannelPermission.RoleId), nameof(ChannelPermission.MemberId),
        nameof(ChannelPermission.AllowPermissions), nameof(ChannelPermission.DenyPermissions),
        nameof(ChannelPermission.AllowModulePermissions), nameof(ChannelPermission.DenyModulePermissions),
    ])]
public partial class FlatChannelPermissionDto
{

}