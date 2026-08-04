namespace Domain;

/// <summary>Who may open a direct message with this account.</summary>
public enum DirectMessagePolicy
{
    Everyone,
    FriendsAndServerMembers,
    Friends,
    Nobody,
}
