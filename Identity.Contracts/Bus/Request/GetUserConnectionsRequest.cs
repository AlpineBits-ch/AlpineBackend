namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "Which external accounts has each of these users linked?" - the data behind the
/// <c>connections</c> profile field (privacy spec T2-17), gated in Social by
/// <c>ConnectionsVisibility</c>.
///
/// <para><b>Batched by design</b>, for the same reason as <see cref="GetUserPrivacySettingsRequest"/>
/// and <see cref="GetUserBirthdaysRequest"/>.</para>
///
/// <para>Steam is the only link type this codebase has (<c>ApplicationUser.SteamId</c>, maintained by
/// the <c>SteamLinkedEvent</c>/<c>SteamUnlinkedEvent</c> pair). The response is a <i>list</i> of
/// typed entries rather than a bare steam id so a second provider can be added without a breaking
/// change.</para>
///
/// <para>A raw SteamID is a cross-platform correlation handle - it resolves to a public Steam profile,
/// a friends list and a play history - so, like the birthday, the handler refuses outright for any
/// account whose <c>ConnectionsVisibility</c> is <c>Nobody</c>, and Social never puts it in a
/// stranger's projection.</para>
/// </summary>
public class GetUserConnectionsRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
