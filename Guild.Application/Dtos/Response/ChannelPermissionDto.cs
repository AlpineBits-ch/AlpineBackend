using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

/// <summary>Category and Member are excluded rather than nested: an overwrite is polymorphic, so
/// at most one of them is ever set, and CategoryId/MemberId already say which. Nesting them would
/// also pull GuildMember's own graph into every guild response - see
/// <see cref="FlatChannelPermissionDto"/> for what expanding these navigations already cost us
/// once.</summary>
[Facet(typeof(ChannelPermission),
    nameof(ChannelPermission.Category), nameof(ChannelPermission.Member),
    NestedFacets = [typeof(ChannelDto), typeof(ChannelPermissionDto), typeof(GuildDto), typeof(RoleDto)])]
public partial class ChannelPermissionDto
{

}

/// <summary>
/// An overwrite reduced to its scoping ids and its allow/deny masks — no Channel, Category, Role
/// or Guild object hanging off it.
///
/// Expanding those navigations is what broke <c>GET /guilds/{id}/me</c>: an overwrite row is
/// polymorphic (exactly one of ChannelId/CategoryId/RoleId/MemberId is set, the rest NULL), so
/// projecting through <c>x.Channel</c> makes EF correlate that branch's collections against a
/// parent whose identifier columns are NULL, and the split-query shaper throws
/// "Nullable object must have a value". It also pulled Channel's own collections — ReadStates,
/// WebhookConfigs and PublicKeys (raw domain entities) — into the response.
///
/// The ids are all a client needs: it already has the channel and role lists from the guild.
/// </summary>
[Facet(typeof(ChannelPermission),
    Include =
    [
        "Id", "CreatedAt", "UpdatedAt",
        nameof(ChannelPermission.ChannelId), nameof(ChannelPermission.CategoryId),
        nameof(ChannelPermission.RoleId), nameof(ChannelPermission.MemberId),
        nameof(ChannelPermission.AllowPermissions), nameof(ChannelPermission.DenyPermissions),
    ])]
public partial class FlatChannelPermissionDto
{

}