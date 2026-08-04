namespace Social.Contracts.Bus.Integration.Response;

public class GetBlockRelationshipsResponse
{
    public ICollection<BlockRelationship> Blocks { get; set; } = new List<BlockRelationship>();
}

/// <summary>
/// One directed block. A blocks B is not the same fact as B blocks A, and both may exist
/// independently - so this is deliberately a pair rather than an unordered set.
/// </summary>
public class BlockRelationship
{
    /// <summary>The user who pressed block.</summary>
    public string BlockerId { get; set; } = null!;

    /// <summary>The user who was blocked, and who must not be told that they were.</summary>
    public string BlockedId { get; set; } = null!;
}
