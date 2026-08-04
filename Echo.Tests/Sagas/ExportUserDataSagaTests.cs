using AppEnvironment;
using Echo.Sagas;
using Echo.Tests.Support;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Microsoft.Extensions.Logging;

namespace Echo.Tests.Sagas;

/// <summary>
/// The export saga, whose deadline deliberately does the opposite of the purge saga's.
/// </summary>
[TestFixture]
public class ExportUserDataSagaTests
{
    private const string ExportId = "dxrq_test_export";
    private const string UserId = "user_export_subject";

    private static readonly string[] AllServices =
        ["identity", "social", "guild", "messaging", "federation", "import", "bots", "isle"];

    private static ExportUserDataSaga Started(
        RecordingLogger<ExportUserDataSaga> logger, out DataExportDeadlineElapsed deadline)
    {
        var saga = new ExportUserDataSaga();
        var (_, armed) = saga.Start(
            new DataExportRequestedEvent { ExportId = ExportId, UserId = UserId }, logger);
        deadline = armed;
        return saga;
    }

    private static void Answer(ExportUserDataSaga saga, RecordingLogger<ExportUserDataSaga> logger, params string[] services)
    {
        foreach (var service in services)
        {
            saga.Handle(new ExportUserDataResponse
            {
                ExportId = ExportId,
                UserId = UserId,
                Service = service,
                FragmentJson = $$"""{"service":"{{service}}"}""",
                RowCounts = new Dictionary<string, int> { [service] = 1 },
            }, logger);
        }
    }

    [Test]
    public void Start_fans_out_and_arms_the_deadline()
    {
        var logger = new RecordingLogger<ExportUserDataSaga>();
        var saga = new ExportUserDataSaga();

        var (command, deadline) = saga.Start(
            new DataExportRequestedEvent { ExportId = ExportId, UserId = UserId }, logger);

        Assert.Multiple(() =>
        {
            Assert.That(command.ExportId, Is.EqualTo(ExportId));
            Assert.That(command.UserId, Is.EqualTo(UserId));
            Assert.That(deadline.ExportId, Is.EqualTo(ExportId));
            Assert.That(deadline.DelayTime, Is.EqualTo(Env.SagaDeadlines.DataExport));
            Assert.That(saga.PendingServices, Is.EquivalentTo(AllServices));
        });
    }

    [Test]
    public void Assembles_once_every_service_has_answered()
    {
        var logger = new RecordingLogger<ExportUserDataSaga>();
        var saga = Started(logger, out _);

        Answer(saga, logger, AllServices[..^1]);
        Assert.That(saga.IsCompleted(), Is.False);

        var assemble = saga.Handle(new ExportUserDataResponse
        {
            ExportId = ExportId,
            UserId = UserId,
            Service = AllServices[^1],
            FragmentJson = "{}",
        }, logger);

        Assert.Multiple(() =>
        {
            Assert.That(assemble, Is.Not.Null);
            Assert.That(assemble!.Fragments, Has.Count.EqualTo(AllServices.Length));
            Assert.That(assemble.Fragments.Select(f => f.Error), Is.All.Null);
            Assert.That(saga.IsCompleted(), Is.True);
        });
    }

    [Test]
    public void Deadline_names_the_services_that_never_answered_and_records_them_in_the_archive()
    {
        var logger = new RecordingLogger<ExportUserDataSaga>();
        var saga = Started(logger, out var deadline);

        Answer(saga, logger, "identity", "social", "guild", "messaging", "federation", "import");

        using var counter = new CounterRecorder(
            PrivacySagaTelemetry.MeterName, PrivacySagaTelemetry.DeadlineExceededCounter);

        var assemble = saga.Handle(deadline, logger);

        var error = logger.AtLevel(LogLevel.Error).SingleOrDefault();
        var missing = assemble!.Fragments.Where(f => f.Error is not null).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain("bots"));
            Assert.That(error.Message, Does.Contain("isle"));
            Assert.That(error.Message, Does.Contain(UserId));
            Assert.That(error.Message, Does.Contain(ExportId));

            Assert.That(counter.TagValues("service"), Is.EquivalentTo(new[] { "bots", "isle" }));
            Assert.That(counter.TagValues("saga"), Is.All.EqualTo("data-export"));

            // The request is resolved rather than left Running, and the archive says what is absent
            // from it - an unexplained gap would be indistinguishable from "that service holds
            // nothing about me".
            Assert.That(assemble, Is.Not.Null);
            Assert.That(assemble.ExportId, Is.EqualTo(ExportId));
            Assert.That(assemble.Fragments, Has.Count.EqualTo(AllServices.Length));
            Assert.That(missing.Select(f => f.Service), Is.EquivalentTo(new[] { "bots", "isle" }));
            Assert.That(
                missing.All(f => f.FragmentJson.Contains("\"complete\": false")),
                Is.True,
                "each stand-in section must say, in the file itself, that it is not the real thing");
            Assert.That(saga.IsCompleted(), Is.True);
        });
    }

    [Test]
    public void A_redelivered_deadline_cannot_assemble_the_archive_twice()
    {
        var logger = new RecordingLogger<ExportUserDataSaga>();
        var saga = Started(logger, out var deadline);

        Answer(saga, logger, "identity");
        saga.Handle(deadline, logger);

        using var counter = new CounterRecorder(
            PrivacySagaTelemetry.MeterName, PrivacySagaTelemetry.DeadlineExceededCounter);

        var again = saga.Handle(deadline, logger);

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.Null);
            Assert.That(counter.Measurements, Is.Empty);
        });
    }

    [Test]
    public void A_duplicate_fragment_is_ignored_rather_than_written_twice()
    {
        var logger = new RecordingLogger<ExportUserDataSaga>();
        var saga = Started(logger, out _);

        Answer(saga, logger, "identity");
        Answer(saga, logger, "identity");

        Assert.Multiple(() =>
        {
            Assert.That(saga.Fragments, Has.Count.EqualTo(1));
            Assert.That(saga.PendingServices, Has.Count.EqualTo(AllServices.Length - 1));
        });
    }

    [Test]
    public void A_deadline_that_arrives_after_a_normal_assembly_does_nothing()
    {
        var logger = new RecordingLogger<ExportUserDataSaga>();
        var saga = Started(logger, out var deadline);

        Answer(saga, logger, AllServices);
        Assert.That(saga.IsCompleted(), Is.True);

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
