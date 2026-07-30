namespace Identity.Contracts.Bus.Events;

/// <summary>Published by Identity's AccountDeletionPurgeSweepService once an account's grace
/// period has elapsed. Echo's AccountDeletionSaga starts on this and fans PurgeUserDataCommand
/// out to every participating service.</summary>
public class AccountPurgeStartedEvent
{
    public string UserId { get; set; }
}
