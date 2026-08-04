namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "Which external accounts has each of these users linked?" - the data behind the
/// <c>connections</c> profile field (privacy spec T2-17), gated in Social by
/// <c>ConnectionsVisibility</c>.
/// </summary>
public class GetUserConnectionsRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
