namespace Guild.Contracts.Bus.Request;

/// <summary>
/// "Which guilds does this person share with each of these people?" - Guild owns the membership
/// table, and Social needs the answer for <c>FriendRequestPolicy.ServerMembers</c> (privacy spec
/// T2-15) and the <c>mutualServers</c> profile field (T2-17).
/// </summary>
public class GetSharedGuildsRequest
{
    /// <summary>The person the intersection is taken from - in Social's callers, the viewer.</summary>
    public string UserId { get; set; } = null!;

    /// <summary>Who to intersect against. Order is not preserved.</summary>
    public ICollection<string> OtherUserIds { get; set; } = new List<string>();
}
