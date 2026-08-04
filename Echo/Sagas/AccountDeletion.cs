using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Echo.Sagas;

/// <summary>The purge saga's deadline.</summary>
public record AccountPurgeDeadlineElapsed() : TimeoutMessage(Env.SagaDeadlines.AccountPurge)
{
    /// <summary>Also the saga id - a purge saga is keyed on the account being purged.</summary>
    public string UserId { get; set; } = null!;

    public int Generation { get; set; }
}

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
    private const string SagaName = "account-purge";

    /// <summary>Every service that owns data belonging to an account.</summary>
    internal static readonly string[] ParticipatingServices =
        ["identity", "social", "guild", "messaging", "federation", "import", "bots", "isle"];

    public string Id { get; set; }
    public List<string> PendingServices { get; set; } = new();

    /// <summary>Which armed deadline is the live one.</summary>
    public int DeadlineGeneration { get; set; }

    /// <summary>How many times this purge has now blown its deadline.</summary>
    public int DeadlineBreaches { get; set; }

    public (PurgeUserDataCommand, AccountPurgeDeadlineElapsed) Start(
        AccountPurgeStartedEvent purgeStarted, ILogger<AccountDeletionSaga> logger)
    {
        Id = purgeStarted.UserId;
        PendingServices = ParticipatingServices.ToList();
        DeadlineGeneration = 1;

        logger.LogInformation(
            "Starting account purge fan-out for {UserId} across {Services}; deadline {Deadline}",
            purgeStarted.UserId, string.Join(", ", PendingServices), Env.SagaDeadlines.AccountPurge);

        return (
            new PurgeUserDataCommand { UserId = purgeStarted.UserId },
            new AccountPurgeDeadlineElapsed { UserId = purgeStarted.UserId, Generation = DeadlineGeneration });
    }

    public AccountDeletionCompletedEvent? Handle(
        [SagaIdentityFrom(nameof(PurgeUserDataCommandResponse.UserId))] PurgeUserDataCommandResponse response,
        ILogger<AccountDeletionSaga> logger)
    {
        // Idempotent against redelivery: an acknowledgement from a service that is no longer pending
        // - a duplicate, or one from a service that is not in the fan-out at all - must not be able
        // to walk the saga to completion on its own.
        if (!PendingServices.Remove(response.Service))
        {
            logger.LogInformation(
                "Ignoring a duplicate {Service} purge acknowledgement for {UserId}", response.Service, Id);
            return null;
        }

        logger.LogInformation(
            "{Service} acknowledged purge for {UserId}, {Remaining} service(s) remaining",
            response.Service, Id, PendingServices.Count);

        if (PendingServices.Count > 0) return null;

        if (DeadlineBreaches > 0)
        {
            // Worth its own line: this is the happy ending of a purge that had already been alerted
            // on, and whoever is looking at that alert needs to see it resolve.
            logger.LogWarning(
                "Account purge for {UserId} completed after {Breaches} missed deadline(s)", Id, DeadlineBreaches);
        }

        logger.LogInformation("Account purge complete for {UserId}", Id);
        MarkCompleted();
        return new AccountDeletionCompletedEvent { UserId = Id };
    }

    /// <summary>The deadline.</summary>
    public AccountPurgeDeadlineElapsed? Handle(
        [SagaIdentityFrom(nameof(AccountPurgeDeadlineElapsed.UserId))] AccountPurgeDeadlineElapsed deadline,
        ILogger<AccountDeletionSaga> logger)
    {
        // Defensive: a completed saga's row is gone and Wolverine drops timeouts aimed at it, so
        // this should be unreachable. If it is ever reached, doing nothing is the right answer.
        if (PendingServices.Count == 0) return null;

        if (deadline.Generation != DeadlineGeneration)
        {
            logger.LogInformation(
                "Ignoring a stale purge deadline (generation {Received}, current {Current}) for {UserId}",
                deadline.Generation, DeadlineGeneration, Id);
            return null;
        }

        DeadlineBreaches++;
        DeadlineGeneration++;

        PrivacySagaTelemetry.ReportDeadlineExceeded(
            logger,
            SagaName,
            Id,
            Id,
            PendingServices,
            Env.SagaDeadlines.AccountPurge,
            DeadlineBreaches,
            "The erasure is NOT complete and has deliberately not been marked complete - these "
            + "services still hold the account's data. Check that each is deployed and that its "
            + "PurgeUserDataCommandHandler is registered; the saga completes on its own once they "
            + "acknowledge.");

        return new AccountPurgeDeadlineElapsed { UserId = Id, Generation = DeadlineGeneration };
    }
}
