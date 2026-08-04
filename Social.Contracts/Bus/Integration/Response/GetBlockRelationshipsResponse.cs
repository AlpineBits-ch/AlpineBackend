namespace Social.Contracts.Bus.Integration.Response;

public class GetBlockRelationshipsResponse
{
    public ICollection<BlockRelationship> Blocks { get; set; } = new List<BlockRelationship>();
}

/// <summary>One directed block.</summary>
public class BlockRelationship
{
    /// <summary>The user who pressed block.</summary>
    public string BlockerId { get; set; } = null!;

    /// <summary>The user who was blocked, and who must not be told that they were.</summary>
    public string BlockedId { get; set; } = null!;
}
