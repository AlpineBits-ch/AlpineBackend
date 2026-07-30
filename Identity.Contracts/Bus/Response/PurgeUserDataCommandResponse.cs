namespace Identity.Contracts.Bus.Response;

/// <summary>Ack sent back by each participating service's PurgeUserDataCommand handler.</summary>
public class PurgeUserDataCommandResponse
{
    public string UserId { get; set; }
    public string Service { get; set; }
}
