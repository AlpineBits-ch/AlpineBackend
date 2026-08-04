namespace Guild.Contracts.Bus.Request;

/// <summary>
/// "Does this user accept DMs from the people they share these servers with?" - the per-guild
/// override behind privacy spec T2-14, asked by Messaging while resolving the
/// <c>FriendsAndServerMembers</c> branch of a recipient's <c>DirectMessagePolicy</c>.
/// </summary>
public class GetGuildDirectMessagePreferenceRequest
{
    public string UserId { get; set; } = null!;

    public ICollection<string> GuildIds { get; set; } = new List<string>();
}
