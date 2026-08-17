namespace Guild.Contracts.Bus.Request;

/// <summary>
/// The public facts behind an invite code, for the card Messaging renders when somebody posts an
/// invite link in a channel.
/// </summary>
public class ResolveInviteCardRequest
{
    /// <summary>The short shareable code from the link, not the invite's id.</summary>
    public string Code { get; set; } = "";
}
