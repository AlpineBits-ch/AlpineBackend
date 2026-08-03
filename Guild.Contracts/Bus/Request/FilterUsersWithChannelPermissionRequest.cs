namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Batched form of <see cref="HasUserPermissionToChannelRequest"/>: resolves the permission for
/// many users against one channel in a single bus round-trip.
///
/// Exists for fan-out paths (Gateway dispatch to installed bots, realtime audience resolution)
/// where the per-user form would mean one request per recipient per event. Guild resolves each
/// user from its own Redis-cached permission set, so the marginal cost per extra user id is a
/// cache read, not a database query.
/// </summary>
public class FilterUsersWithChannelPermissionRequest
{
    public required string ChannelId { get; set; }
    public required IReadOnlyCollection<string> UserIds { get; set; }
    public ExternalPermission Permission { get; set; }

    public override string ToString() =>
        $"FilterUsersWithChannelPermissionRequest(ChannelId: {ChannelId}, UserIds: {UserIds.Count}, Permission: {Permission})";
}
