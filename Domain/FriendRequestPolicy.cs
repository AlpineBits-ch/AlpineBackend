namespace Domain;

/// <summary>Who may send this account a friend request.</summary>
public enum FriendRequestPolicy
{
    Everyone,
    FriendsOfFriends,
    ServerMembers,
    Nobody,
}
