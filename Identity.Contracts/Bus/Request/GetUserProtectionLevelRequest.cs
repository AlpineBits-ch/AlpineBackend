using Domain;

namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "How does this account admit new devices?" - Identity owns the setting, and Messaging has to know
/// it before it can tell an approving client whether a join request may be satisfied by cryptographic
/// proof alone or needs a human as well.
/// </summary>
public class GetUserProtectionLevelRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
