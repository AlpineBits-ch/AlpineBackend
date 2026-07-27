namespace Guild.Contracts.Bus.Response;

public class ResolveInstallablePermissionsResponse
{
    public bool HasManageGuild { get; set; }
    public ulong ClampedPermissions { get; set; }
}
