namespace Guild.Contracts.Bus.Response;

/// <summary>
/// The subset of the requested user ids that hold the requested permission on the channel.
/// An unknown channel yields an empty set - callers fail closed.
/// </summary>
public class FilterUsersWithChannelPermissionResponse
{
    public required string ChannelId { get; set; }
    public required IReadOnlyCollection<string> AllowedUserIds { get; set; }
}
