namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "Which devices do these users have?" - Identity owns the device table, and Messaging needs the
/// full roster to answer a question it could not previously ask: whether every device of every
/// member actually received a Welcome.
///
/// <para>Deliberately separate from <c>ConsumeMlsDeviceTokensForUserRequest</c>, which is the only
/// other way to enumerate devices and which <i>consumes</i> a key package per call - so using it to
/// find out who is out there is destructive by construction.</para>
/// </summary>
public class GetUserDevicesRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
