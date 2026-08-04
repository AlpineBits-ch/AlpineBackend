namespace Social.Contracts.Bus.Integration.Request;

/// <summary>
/// "Who among these people has blocked whom?" - Social owns the block list, and every other service
/// has to consult it before it lets one user reach another.
/// </summary>
public class GetBlockRelationshipsRequest
{
    /// <summary>Blocks are returned when a listed user appears on <i>either</i> side, so a caller
    /// can answer both "did A block B" and "did B block A" from one round trip.</summary>
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
