using Social.Domain.Enums;

namespace Social.Api.Dtos.Realtime;

/// <summary>Body of every <c>social.*</c> relationship push.</summary>
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
