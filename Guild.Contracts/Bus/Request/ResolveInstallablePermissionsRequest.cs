namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Checks whether an installer can add a bot to a guild, and clamps the bot's requested permission
/// bitmask down to what the installer actually holds (privilege-escalation guard).
/// </summary>
public class ResolveInstallablePermissionsRequest
{
    public string InstallerUserId { get; set; }
    public string GuildId { get; set; }
    public ulong RequestedPermissions { get; set; }
}
