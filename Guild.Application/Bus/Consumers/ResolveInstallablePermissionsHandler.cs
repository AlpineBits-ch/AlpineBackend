using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;

namespace Guild.Application.Bus.Consumers;

public class ResolveInstallablePermissionsHandler
{
    public static async Task<ResolveInstallablePermissionsResponse> Handle(
        ResolveInstallablePermissionsRequest request,
        GuildPermissionService permissionService)
    {
        var hasManageGuild = await permissionService.CanUserPerformActionOnGuildAsync(
            request.InstallerUserId, request.GuildId, Permissions.ManageGuild);

        if (!hasManageGuild)
        {
            return new ResolveInstallablePermissionsResponse { HasManageGuild = false, ClampedPermissions = 0 };
        }

        var clamped = await permissionService.ClampToGrantableAsync(
            request.InstallerUserId, request.GuildId, (Permissions)request.RequestedPermissions);

        return new ResolveInstallablePermissionsResponse
        {
            HasManageGuild = true,
            ClampedPermissions = (ulong)clamped,
        };
    }
}
