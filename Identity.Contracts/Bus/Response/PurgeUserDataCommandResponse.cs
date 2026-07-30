namespace Identity.Contracts.Bus.Response;

/// <summary>Ack sent back by each participating service's PurgeUserDataCommand handler. Service
/// is a fixed lowercase identifier ("identity", "social", "guild", "messaging", "federation",
/// "import") - AccountDeletionSaga uses it to track which of the known participants have
/// responded.</summary>
public class PurgeUserDataCommandResponse
{
    public string UserId { get; set; }
    public string Service { get; set; }
}
