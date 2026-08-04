using AppEnvironment;
using Echo.Sagas;
using Echo.Tests.Support;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Microsoft.Extensions.Logging;

namespace Echo.Tests.Sagas;

/// <summary>The purge saga, and specifically the thing it must never do.</summary>
[TestFixture]
public class AccountDeletionSagaTests
{
    private const string UserId = "user_purge_target";

    private static readonly string[] AllServices =
        ["identity", "social", "guild", "messaging", "federation", "import", "bots", "isle"];

    private static AccountDeletionSaga Started(RecordingLogger<AccountDeletionSaga> logger, out AccountPurgeDeadlineElapsed deadline)
    {
        var saga = new AccountDeletionSaga();
        var (_, armed) = saga.Start(new AccountPurgeStartedEvent { UserId = UserId }, logger);
        deadline = armed;
        return saga;
    }

    private static void Acknowledge(AccountDeletionSaga saga, RecordingLogger<AccountDeletionSaga> logger, params string[] services)
    {
        foreach (var service in services)
        {
            saga.Handle(new PurgeUserDataCommandResponse { UserId = UserId, Service = service }, logger);
        }
    }

    [Test]
    public void Start_fans_out_and_arms_the_first_deadline()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = new AccountDeletionSaga();

        var (command, deadline) = saga.Start(new AccountPurgeStartedEvent { UserId = UserId }, logger);

        Assert.Multiple(() =>
        {
            Assert.That(command.UserId, Is.EqualTo(UserId));
            Assert.That(saga.PendingServices, Is.EquivalentTo(AllServices));
            Assert.That(deadline.UserId, Is.EqualTo(UserId));
            Assert.That(deadline.Generation, Is.EqualTo(1));
            Assert.That(deadline.DelayTime, Is.EqualTo(Env.SagaDeadlines.AccountPurge));
            Assert.That(saga.IsCompleted(), Is.False);
        });
    }

    [Test]
    public void Completes_only_once_every_service_has_acknowledged()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out _);

        foreach (var service in AllServices[..^1])
        {
            var interim = saga.Handle(new PurgeUserDataCommandResponse { UserId = UserId, Service = service }, logger);
            Assert.That(interim, Is.Null, $"the saga completed before {service} was the last one left");
            Assert.That(saga.IsCompleted(), Is.False);
        }

        var completed = saga.Handle(
            new PurgeUserDataCommandResponse { UserId = UserId, Service = AllServices[^1] }, logger);

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.Not.Null);
            Assert.That(completed!.UserId, Is.EqualTo(UserId));
            Assert.That(saga.IsCompleted(), Is.True);
        });
    }

    [Test]
    public void Deadline_names_every_service_that_has_not_acknowledged()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out var deadline);

        Acknowledge(saga, logger, "identity", "social", "guild", "messaging", "federation", "import");

        using var counter = new CounterRecorder(
            PrivacySagaTelemetry.MeterName, PrivacySagaTelemetry.DeadlineExceededCounter);

        var rearmed = saga.Handle(deadline, logger);

        var error = logger.AtLevel(LogLevel.Error).SingleOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Not.Null, "the deadline must log at Error - it is the only signal an operator gets");
            Assert.That(error!.Message, Does.Contain("bots"));
            Assert.That(error.Message, Does.Contain("isle"));
            Assert.That(error.Message, Does.Contain(UserId), "the subject's id is the whole point of the diagnostic");

            // The services that DID acknowledge must not be named - an alert that lists innocent
            // services is an alert somebody stops reading.
            Assert.That(error.Message, Does.Not.Contain("federation"));
            Assert.That(error.Message, Does.Not.Contain("guild"));

            Assert.That(counter.Measurements, Has.Count.EqualTo(2));
            Assert.That(counter.TagValues("service"), Is.EquivalentTo(new[] { "bots", "isle" }));
            Assert.That(counter.TagValues("saga"), Is.All.EqualTo("account-purge"));

            Assert.That(rearmed, Is.Not.Null, "the deadline re-arms so a stalled erasure keeps announcing itself");
            Assert.That(rearmed!.Generation, Is.EqualTo(2));
            Assert.That(saga.DeadlineBreaches, Is.EqualTo(1));
        });
    }

    [Test]
    public void Deadline_never_completes_the_purge_or_claims_it_finished()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out var deadline);

        Acknowledge(saga, logger, "identity", "social", "guild", "messaging", "federation", "import");

        saga.Handle(deadline, logger);

        Assert.Multiple(() =>
        {
            // Both halves of "a purge must not report an erasure that did not happen": the saga row
            // survives (so it still remembers who is holding data) and no completion event is
            // published (so nothing downstream records the deletion as done).
            Assert.That(saga.IsCompleted(), Is.False);
            Assert.That(saga.PendingServices, Is.EquivalentTo(new[] { "bots", "isle" }));
        });
    }

    [Test]
    public void A_late_acknowledgement_after_the_deadline_still_completes_the_purge()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out var deadline);

        Acknowledge(saga, logger, "identity", "social", "guild", "messaging", "federation", "import");
        saga.Handle(deadline, logger);

        saga.Handle(new PurgeUserDataCommandResponse { UserId = UserId, Service = "bots" }, logger);
        var completed = saga.Handle(new PurgeUserDataCommandResponse { UserId = UserId, Service = "isle" }, logger);

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.Not.Null, "recovery is the whole reason the saga is kept alive at the deadline");
            Assert.That(saga.IsCompleted(), Is.True);
            Assert.That(
                logger.AtLevel(LogLevel.Warning).Any(l => l.Message.Contains("after 1 missed deadline")),
                Is.True,
                "an alert that fired needs a matching line saying it resolved");
        });
    }

    [Test]
    public void A_redelivered_deadline_from_an_earlier_generation_is_inert()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out var deadline);

        Acknowledge(saga, logger, "identity", "social", "guild", "messaging", "federation", "import");

        saga.Handle(deadline, logger);

        using var counter = new CounterRecorder(
            PrivacySagaTelemetry.MeterName, PrivacySagaTelemetry.DeadlineExceededCounter);

        // The broker redelivers the very same timeout envelope.
        var again = saga.Handle(deadline, logger);

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.Null, "a stale timeout must not re-arm a second parallel deadline");
            Assert.That(saga.DeadlineBreaches, Is.EqualTo(1), "a duplicate must not inflate the breach count");
            Assert.That(counter.Measurements, Is.Empty, "a duplicate must not page anybody a second time");
            Assert.That(saga.IsCompleted(), Is.False);
        });
    }

    [Test]
    public void A_duplicate_acknowledgement_cannot_complete_the_saga_on_its_own()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out _);

        Acknowledge(saga, logger, AllServices[..^1]);

        // "isle" is still outstanding; a second copy of an earlier ack, or one from a service that is
        // not in the fan-out at all, must not be mistaken for it.
        var duplicate = saga.Handle(new PurgeUserDataCommandResponse { UserId = UserId, Service = "guild" }, logger);
        var stranger = saga.Handle(new PurgeUserDataCommandResponse { UserId = UserId, Service = "nowhere" }, logger);

        Assert.Multiple(() =>
        {
            Assert.That(duplicate, Is.Null);
            Assert.That(stranger, Is.Null);
            Assert.That(saga.IsCompleted(), Is.False);
            Assert.That(saga.PendingServices, Is.EquivalentTo(new[] { "isle" }));
        });
    }

    [Test]
    public void A_deadline_that_arrives_with_nothing_outstanding_does_nothing()
    {
        var logger = new RecordingLogger<AccountDeletionSaga>();
        var saga = Started(logger, out var deadline);

        Acknowledge(saga, logger, AllServices);

        using var counter = new CounterRecorder(
            PrivacySagaTelemetry.MeterName, PrivacySagaTelemetry.DeadlineExceededCounter);

        var result = saga.Handle(deadline, logger);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(counter.Measurements, Is.Empty);
            Assert.That(logger.AtLevel(LogLevel.Error), Is.Empty);
        });
    }
}
