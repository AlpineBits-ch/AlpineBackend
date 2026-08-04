namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What has each of these accounts consented to, and who may reach them?" - Identity owns the
/// record, and Messaging, Social, Guild and Isle all have to resolve it before they may deliver a
/// DM, a friend request, a presence update or a positional voice registration.
///
/// <para><b>Batched by design.</b> Every caller resolves policy for a <i>set</i> of people - the
/// recipients of a group DM, the members of a channel fan-out - and the per-user shape is exactly
/// what makes <c>ConversationEndpoints</c> issue N sequential bus calls today.</para>
///
/// <para>An id with no stored row comes back with the restrictive defaults rather than being
/// omitted, so a caller cannot mistake "no answer" for "no restriction". A caller that gets no
/// response at all must fail closed on its own - see the cross-cutting rules in
/// docs/specs/privacy.md.</para>
/// </summary>
public class GetUserPrivacySettingsRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
