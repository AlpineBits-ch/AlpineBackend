namespace Identity.Domain.Enums;

public enum UserStatus
{
    Active,
    Inactive,
    Banned,

    /// <summary>Deletion requested; still fully account-shaped in every other service, but
    /// login is blocked (see ApplicationUser.IsSigninAllowed) and the grace period countdown
    /// (PurgeScheduledAt) is running. Reversible via CancelDeletionRequest.</summary>
    PendingDeletion,

    /// <summary>
    /// Grace period elapsed; the cross-service purge fan-out (AccountDeletionSaga) is in flight.
    /// </summary>
    PurgeInProgress,

    /// <summary>Terminal tombstone state (see ApplicationUser.Tombstone) - the row and its Id
    /// live on forever so every other service's UserId/AuthorId references keep resolving, but
    /// all personal data has been scrubbed.</summary>
    Deleted
}