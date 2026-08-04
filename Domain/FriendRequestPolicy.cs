namespace Domain;

/// <summary>
/// Who may send this account a friend request.
///
/// <para>Defaults to <see cref="Everyone"/> - the block list is the escape hatch, and a stricter
/// default would make the product unusable for the people it is meant to connect. Refusals are
/// deliberately indistinguishable from "no such user" at the enforcement site so the endpoint is
/// not an enumeration oracle.</para>
/// </summary>
public enum FriendRequestPolicy
{
    Everyone,
    FriendsOfFriends,
    ServerMembers,
    Nobody,
}
