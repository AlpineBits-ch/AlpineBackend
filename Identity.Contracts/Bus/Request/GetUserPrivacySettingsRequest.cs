namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What has each of these accounts consented to, and who may reach them?" - Identity owns the
/// record, and Messaging, Social, Guild and Isle all have to resolve it before they may deliver a
/// DM, a friend request, a presence update or a positional voice registration.
/// </summary>
public class GetUserPrivacySettingsRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
