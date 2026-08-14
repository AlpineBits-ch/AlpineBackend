namespace Guild.Contracts.Bus.Response;

/// <summary>
/// The subset of the requested channel ids the user holds the requested permission on.
/// </summary>
public class FilterChannelsWithUserPermissionResponse
{
    public required string UserId { get; set; }
    public required IReadOnlyCollection<string> AllowedChannelIds { get; set; }
}
