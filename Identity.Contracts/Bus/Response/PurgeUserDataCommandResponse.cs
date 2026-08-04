namespace Identity.Contracts.Bus.Response;

/// <summary>Ack sent back by each participating service's PurgeUserDataCommand handler. Service
/// is a fixed lowercase identifier ("identity", "social", "guild", "messaging", "federation",
/// "import", "bots", "isle") - AccountDeletionSaga uses it to track which of the known
/// participants have responded, so the string here must match the entry in that list exactly.</summary>
public class PurgeUserDataCommandResponse
{
    public string UserId { get; set; }
    public string Service { get; set; }
}
