namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Batched form of <see cref="HasUserPermissionToChannelRequest"/>: resolves the permission for
/// many users against one channel in a single bus round-trip.
/// </summary>
public class FilterUsersWithChannelPermissionRequest
{
    public required string ChannelId { get; set; }
    public required IReadOnlyCollection<string> UserIds { get; set; }
    public ExternalPermission Permission { get; set; }

    public override string ToString() =>
        $"FilterUsersWithChannelPermissionRequest(ChannelId: {ChannelId}, UserIds: {UserIds.Count}, Permission: {Permission})";
}
