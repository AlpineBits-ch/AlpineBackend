namespace Identity.Contracts.Bus.Events;

/// <summary>Published by Echo's AccountDeletionSaga once every participating service has
/// acknowledged PurgeUserDataCommand. Nothing consumes this yet - it's the natural hook for
/// future audit-log/notification work, and a clean saga-completion signal for tests.</summary>
public class AccountDeletionCompletedEvent
{
    public string UserId { get; set; }
}
