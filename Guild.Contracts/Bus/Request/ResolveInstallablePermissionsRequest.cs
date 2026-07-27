namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Checks whether an installer can add a bot to a guild, and clamps the bot's requested
/// permission bitmask down to what the installer actually holds (privilege-escalation
/// guard). Uses a raw ulong bitmask rather than <see cref="ExternalPermission"/> because,
/// unlike every other permission check in this contract, this one mirrors Discord's OAuth2
/// "permissions" query parameter - a composed bitmask, not a single yes/no permission.
/// </summary>
public class ResolveInstallablePermissionsRequest
{
    public string InstallerUserId { get; set; }
    public string GuildId { get; set; }
    public ulong RequestedPermissions { get; set; }
}
