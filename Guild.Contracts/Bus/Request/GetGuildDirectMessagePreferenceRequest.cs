namespace Guild.Contracts.Bus.Request;

/// <summary>
/// "Does this user accept DMs from the people they share these servers with?" - the per-guild
/// override behind privacy spec T2-14, asked by Messaging while resolving the
/// <c>FriendsAndServerMembers</c> branch of a recipient's <c>DirectMessagePolicy</c>.
///
/// <para><see cref="UserId"/> is the <b>recipient</b> - the person whose consent is being checked -
/// never the initiator. The initiator's own settings say nothing about who may reach them.</para>
///
/// <para><see cref="GuildIds"/> narrows the answer to a candidate set (normally the guilds the
/// initiator is in). <b>Leave it empty to get every guild the user is a member of</b>, which is what
/// a caller that does not yet know the shared set wants; it can then intersect client-side.</para>
///
/// <para>Guilds the user is not a member of are omitted from the response rather than returned
/// false - "not a member" is not a preference, and a caller asking "is there a shared guild that
/// admits this DM" gets the right answer either way from
/// <c>Preferences.Any(p =&gt; p.AllowDirectMessages)</c>.</para>
/// </summary>
public class GetGuildDirectMessagePreferenceRequest
{
    public string UserId { get; set; } = null!;

    public ICollection<string> GuildIds { get; set; } = new List<string>();
}
