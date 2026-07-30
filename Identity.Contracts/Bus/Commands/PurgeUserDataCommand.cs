namespace Identity.Contracts.Bus.Commands;

/// <summary>Fanned out (broadcast, not point-to-point) by Echo's AccountDeletionSaga to every
/// participating service - each has its own handler bound to its own queue via Wolverine's
/// conventional routing, so a single publish reaches all of them. Handlers must be idempotent:
/// redelivery (retry, or a crash between handling and acking) must be a safe no-op.</summary>
public class PurgeUserDataCommand
{
    public string UserId { get; set; }
}
