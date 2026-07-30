using Social.Domain.Enums;

namespace Social.Api.Dtos.Realtime;

/// <summary>
/// Body of every <c>social.*</c> relationship push. Deliberately written from the *recipient's*
/// point of view: the same lifecycle change is pushed to both users with different values, so a
/// client never has to work out which of the two mirrored Relationship rows is "its" row.
///
/// <see cref="RelationshipId"/> is the recipient's own row id (the one it can pass straight back
/// to /accept, /reject or /revoke), <see cref="Status"/> is that row's status after the change,
/// and the User/Profile/UserName fields describe the *other* party.
///
/// Status serializes as its name ("PendingIncoming", "PendingOutgoing", "Friends", "None") - the
/// hub's JSON protocol is registered with a JsonStringEnumConverter in Program.cs.
/// </summary>
public record FriendRelationshipPayload
{
    /// <summary>The recipient's own Relationship row id (prefix <c>rlsp_</c>).</summary>
    public required string RelationshipId { get; init; }

    /// <summary>The recipient's own view of the relationship after this change.</summary>
    public required RelationshipStatus Status { get; init; }

    /// <summary>User id of the other party.</summary>
    public required string UserId { get; init; }

    /// <summary>Profile id (prefix <c>prfl_</c>) of the other party.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Display name of the other party.</summary>
    public required string UserName { get; init; }
}
