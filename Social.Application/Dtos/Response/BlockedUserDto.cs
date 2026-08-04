namespace Social.Api.Dtos.Response;

/// <summary>One entry of the caller's own block list.</summary>
public class BlockedUserDto
{
    /// <summary>The caller's <c>Relationship</c> row that carries the block (prefix <c>rlsp_</c>).
    /// Only the blocker has one - the blocked party never gains a mirrored row, which is what keeps
    /// the block invisible to them.</summary>
    public required string RelationshipId { get; init; }

    public required string ProfileId { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string AvatarUrl { get; init; }

    /// <summary>When the row last changed, i.e. when the block was placed.</summary>
    public required DateTimeOffset BlockedAt { get; init; }
}

/// <summary>A page of <see cref="BlockedUserDto"/>, keyset-paged like the forum listing.</summary>
public class BlockedUsersPageDto
{
    public IReadOnlyList<BlockedUserDto> Blocked { get; init; } = [];

    /// <summary>Opaque; pass back as <c>?cursor=</c>. Null on the last page.</summary>
    public string? NextCursor { get; init; }
}
