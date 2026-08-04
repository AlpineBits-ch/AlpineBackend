using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Echo.Sagas;

/// <summary>
/// The purge saga's deadline. Derives from <see cref="TimeoutMessage"/>, which is what tells
/// Wolverine to schedule it for later delivery and - crucially - to <b>discard it if the saga is
/// already gone</b>, so a purge that finished normally never has to deal with its own timeout.
///
/// <para><see cref="Generation"/> makes a redelivered timeout inert. Each armed deadline carries the
/// generation it was armed with, and the saga only acts on a timeout matching its current one, so a
/// duplicate delivery of the same deadline cannot double-count a breach or double-page an
/// operator.</para>
/// </summary>
public record AccountPurgeDeadlineElapsed() : TimeoutMessage(Env.SagaDeadlines.AccountPurge)
{
    /// <summary>Also the saga id - a purge saga is keyed on the account being purged.</summary>
    public string UserId { get; set; } = null!;

    public int Generation { get; set; }
}

/// <summary>
/// Orchestrates the cross-service account purge, mirroring UserRegistrationSaga's shape but for
/// the inverse lifecycle event: starts on AccountPurgeStartedEvent (published by Identity once
/// an account's grace period has elapsed - see AccountDeletionPurgeSweepService), fans
/// PurgeUserDataCommand out to every participating service in one publish (each service has its
/// own handler bound to its own queue via Wolverine's conventional routing, so a single publish
/// reaches all of them), and completes once every one of them has acknowledged.
///
/// Deliberately does NOT special-case Identity's own tombstone step - Identity is just another
/// participant (Identity.Application.Commands.PurgeUserDataCommandHandler) that happens to run
/// in-process with the sweep that triggered this saga. Keeping the fan-out list uniform is what
/// made adding Bots and Isle a one-line change here plus a handler in each.
///
/// <para><b>The deadline never completes the purge, and that asymmetry with the export saga is the
/// whole point.</b> When <see cref="AccountPurgeDeadlineElapsed"/> arrives with services still
/// outstanding, this saga logs, counts and reports - and then goes on waiting. It does not call
/// <c>MarkCompleted</c> and it does not publish <c>AccountDeletionCompletedEvent</c>, because that
/// event is the system's assertion that an erasure happened. Publishing it while Bots or Isle still
/// holds the account's data would convert a visible, fixable stall into a false statement to the
/// data subject and to any regulator who later asks - and, worse, it would delete the saga row that
/// records exactly which service still has the data. A stalled purge that is screaming once an hour
/// is recoverable: deploy the missing handler, replay the command, and the late acknowledgement
/// still completes the saga normally. A purge that declared victory is not recoverable, because
/// nothing remembers what was left behind. The export saga makes the opposite call for the opposite
/// reason; see its remarks.</para>
///
/// <para>The deadline re-arms itself indefinitely rather than firing once. An erasure that has not
/// completed is a live compliance exposure for as long as it lasts, so it keeps announcing itself
/// until somebody fixes it; one scheduled message per hour per stalled purge is a rounding error
/// next to a silent one. <see cref="DeadlineBreaches"/> is carried on the saga so each alert says
/// how long this has been going on.</para>
/// </summary>
public class AccountDeletionSaga : Saga
{
    private const string SagaName = "account-purge";

    /// <summary>
    /// Every service that owns data belonging to an account.
    ///
    /// <para>Bots and Isle were the two deliberate omissions of the first iteration, on the grounds
    /// that neither is "just anonymize a row" - a bot application cannot simply be renamed when its
    /// owner leaves, and an Isle player is keyed by Steam id with the account link as a nullable
    /// side-reference. T1-9 of docs/specs/privacy.md is that follow-up: Isle unlinks the player and
    /// drops its positional/voice state, Bots disables the owner's applications and removes their
    /// guild installations so none is left running with nobody able to administer it.</para>
    ///
    /// <para>A service named here that never acknowledges leaves the saga pending. That is still the
    /// correct failure mode - a purge that quietly considers itself finished while one service holds
    /// the data is the failure this list exists to prevent - but it is no longer a <i>silent</i>
    /// one: see <see cref="Handle(AccountPurgeDeadlineElapsed, ILogger{AccountDeletionSaga})"/>.
    /// Every environment running this saga still has to be running all eight processes; the
    /// difference is that an environment which is not now says so out loud.</para>
    /// </summary>
    internal static readonly string[] ParticipatingServices =
        ["identity", "social", "guild", "messaging", "federation", "import", "bots", "isle"];

    public string Id { get; set; }
    public List<string> PendingServices { get; set; } = new();

    /// <summary>Which armed deadline is the live one. Incremented every time the deadline is
    /// re-armed, so a redelivered timeout from an earlier generation is recognized and ignored.</summary>
    public int DeadlineGeneration { get; set; }

    /// <summary>How many times this purge has now blown its deadline. Reported in every alert so the
    /// person reading it can tell a service that is thirty seconds late from one that has been
    /// missing for a week.</summary>
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

    /// <summary>
    /// The deadline. Reports exactly which services have not acknowledged, then re-arms.
    ///
    /// <para>Returns a fresh <see cref="AccountPurgeDeadlineElapsed"/> rather than ending the saga -
    /// see this class's remarks for why a purge must never mark itself complete on a timeout. The
    /// returned message carries the new generation, which is what makes the previous one - including
    /// any redelivered copy of it - inert.</para>
    /// </summary>
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
