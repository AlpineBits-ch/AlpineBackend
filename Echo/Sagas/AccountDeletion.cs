using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Echo.Sagas;

/// <summary>
/// Orchestrates the cross-service account purge, mirroring UserRegistrationSaga's shape but for the
/// inverse lifecycle event: starts on AccountPurgeStartedEvent (published by Identity once an
/// account's grace period has elapsed - see AccountDeletionPurgeSweepService), fans
/// PurgeUserDataCommand out to every participating service in one publish (each service has its own
/// handler bound to its own queue via Wolverine's conventional routing, so a single publish reaches
/// all of them), and completes once every one of them has acknowledged.
/// </summary>
public class AccountDeletionSaga : Saga
{
    /// <summary>Known participants as of this iteration.</summary>
    private static readonly string[] ParticipatingServices =
        ["identity", "social", "guild", "messaging", "federation", "import"];

    public string Id { get; set; }
    public List<string> PendingServices { get; set; } = new();

    public PurgeUserDataCommand Start(AccountPurgeStartedEvent purgeStarted, ILogger<AccountDeletionSaga> logger)
    {
        Id = purgeStarted.UserId;
        PendingServices = ParticipatingServices.ToList();

        logger.LogInformation(
            "Starting account purge fan-out for {UserId} across {Services}",
            purgeStarted.UserId, string.Join(", ", PendingServices));

        return new PurgeUserDataCommand { UserId = purgeStarted.UserId };
    }

    public AccountDeletionCompletedEvent? Handle(
        [SagaIdentityFrom(nameof(PurgeUserDataCommandResponse.UserId))] PurgeUserDataCommandResponse response,
        ILogger<AccountDeletionSaga> logger)
    {
        // Idempotent against redelivery of the same ack: Remove on a value not present is a no-op.
        PendingServices.Remove(response.Service);

        logger.LogInformation(
            "{Service} acknowledged purge for {UserId}, {Remaining} service(s) remaining",
            response.Service, Id, PendingServices.Count);

        if (PendingServices.Count > 0) return null;

        logger.LogInformation("Account purge complete for {UserId}", Id);
        MarkCompleted();
        return new AccountDeletionCompletedEvent { UserId = Id };
    }
}
