namespace Social.Contracts.Bus.Integration.Request;

/// <summary>
/// "Who among these people has blocked whom?" - Social owns the block list, and every other
/// service has to consult it before it lets one user reach another.
///
/// <para>Batched by design. Every caller resolves reachability for a set of people at once (a
/// conversation's members, a mention fan-out, a member list), and the per-user shape is what makes
/// the existing profile lookups in ConversationEndpoints issue one bus call per member.</para>
/// </summary>
public class GetBlockRelationshipsRequest
{
    /// <summary>Blocks are returned when a listed user appears on <i>either</i> side, so a caller
    /// can answer both "did A block B" and "did B block A" from one round trip.</summary>
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
