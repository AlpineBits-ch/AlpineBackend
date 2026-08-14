namespace Guild.Contracts.Bus.Request;

/// <summary>
/// The other axis of <see cref="FilterUsersWithChannelPermissionRequest"/>: one user, many
/// channels, one bus round-trip.
/// </summary>
public class FilterChannelsWithUserPermissionRequest
{
    public required string UserId { get; set; }
    public required IReadOnlyCollection<string> ChannelIds { get; set; }
    public ExternalPermission Permission { get; set; }

    public override string ToString() =>
        $"FilterChannelsWithUserPermissionRequest(UserId: {UserId}, ChannelIds: {ChannelIds.Count}, Permission: {Permission})";
}
