namespace Guild.Contracts.Bus.Request;

/// <summary>
/// "Which guilds does this person share with each of these people?" - Guild owns the membership
/// table, and Social needs the answer for <c>FriendRequestPolicy.ServerMembers</c> (privacy spec
/// T2-15) and the <c>mutualServers</c> profile field (T2-17).
///
/// <para><b>Batched by design.</b> Social calls this once per profile-list render, so the per-pair
/// shape would put a bus round trip on every row.</para>
///
/// <para><b>This is itself a privacy-sensitive query</b>, not a lookup helper. Guild co-membership
/// is exactly the fact <c>MutualServersVisibility</c> exists to control, so the contract is
/// deliberately shaped to answer <i>only about pairs</i>: there is no "every guild user X is in"
/// mode and one must never be added here. Every guild in the response is one both parties are
/// already in, so neither learns anything about the other they could not learn by opening the
/// member list.</para>
///
/// <para>An id equal to <see cref="UserId"/> is ignored rather than answered. The intersection of a
/// user with themselves is their complete guild list, which is the enumeration this shape otherwise
/// hands out for free - and the caller is a service, not necessarily that user.</para>
/// </summary>
public class GetSharedGuildsRequest
{
    /// <summary>The person the intersection is taken from - in Social's callers, the viewer.</summary>
    public string UserId { get; set; } = null!;

    /// <summary>Who to intersect against. Order is not preserved.</summary>
    public ICollection<string> OtherUserIds { get; set; } = new List<string>();
}
