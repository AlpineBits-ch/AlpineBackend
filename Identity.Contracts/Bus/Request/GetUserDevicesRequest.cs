namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "Which devices do these users have?" - Identity owns the device table, and Messaging needs the
/// full roster to answer a question it could not previously ask: whether every device of every
/// member actually received a Welcome.
/// </summary>
public class GetUserDevicesRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
