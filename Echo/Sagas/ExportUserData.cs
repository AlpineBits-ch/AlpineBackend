using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Echo.Sagas;

/// <summary>
/// The export saga's deadline, and the read-side twin of <see cref="AccountPurgeDeadlineElapsed"/>.
/// </summary>
public record DataExportDeadlineElapsed() : TimeoutMessage(Env.SagaDeadlines.DataExport)
{
    /// <summary>Also the saga id.</summary>
    public string ExportId { get; set; } = null!;
}

/// <summary>Orchestrates the cross-service data export (GDPR Art.</summary>
public class ExportUserDataSaga : Saga
{
    private const string SagaName = "data-export";

    /// <summary>Every service that holds data belonging to an account.</summary>
    private static readonly string[] ParticipatingServices =
        ["identity", "social", "guild", "messaging", "federation", "import", "bots", "isle"];

    /// <summary>The export id, so two exports for different accounts - or a second export for the
    /// same account - never share a saga.</summary>
    public string Id { get; set; }

    public string UserId { get; set; } = null!;

    public List<string> PendingServices { get; set; } = new();

    public List<UserDataExportFragment> Fragments { get; set; } = new();

    /// <summary>Set the moment the deadline resolves this export.</summary>
    public bool ResolvedByDeadline { get; set; }

    public (ExportUserDataCommand, DataExportDeadlineElapsed) Start(
        DataExportRequestedEvent requested, ILogger<ExportUserDataSaga> logger)
    {
        Id = requested.ExportId;
        UserId = requested.UserId;
        PendingServices = ParticipatingServices.ToList();

        logger.LogInformation(
            "Starting data export fan-out {ExportId} for {UserId} across {Services}; deadline {Deadline}",
            requested.ExportId, requested.UserId, string.Join(", ", PendingServices),
            Env.SagaDeadlines.DataExport);

        return (
            new ExportUserDataCommand { ExportId = requested.ExportId, UserId = requested.UserId },
            new DataExportDeadlineElapsed { ExportId = requested.ExportId });
    }

    public AssembleUserDataExportCommand? Handle(
        [SagaIdentityFrom(nameof(ExportUserDataResponse.ExportId))] ExportUserDataResponse response,
        ILogger<ExportUserDataSaga> logger)
    {
        // Idempotent against redelivery of the same fragment: Remove on a value not present is a
        // no-op, and the guard below keeps the duplicate out of the archive rather than writing the
        // same service's section twice.
        if (!PendingServices.Remove(response.Service))
        {
            logger.LogInformation(
                "Ignoring a duplicate {Service} fragment for export {ExportId}", response.Service, Id);
            return null;
        }

        Fragments.Add(new UserDataExportFragment
        {
            Service = response.Service,
            FragmentJson = response.FragmentJson,
            RowCounts = response.RowCounts,
            Error = response.Error,
        });

        if (response.Error is not null)
        {
            logger.LogWarning(
                "{Service} could not produce its fragment for export {ExportId}: {Error}",
                response.Service, Id, response.Error);
        }

        logger.LogInformation(
            "{Service} answered export {ExportId}, {Remaining} service(s) remaining",
            response.Service, Id, PendingServices.Count);

        if (PendingServices.Count > 0) return null;

        logger.LogInformation("Data export {ExportId} collected all fragments, assembling", Id);
        MarkCompleted();

        return new AssembleUserDataExportCommand
        {
            ExportId = Id,
            UserId = UserId,
            Fragments = Fragments,
        };
    }

    /// <summary>The deadline.</summary>
    public AssembleUserDataExportCommand? Handle(
        [SagaIdentityFrom(nameof(DataExportDeadlineElapsed.ExportId))] DataExportDeadlineElapsed deadline,
        ILogger<ExportUserDataSaga> logger)
    {
        // A completed saga's row is gone and Wolverine drops timeouts aimed at a saga that no longer
        // exists, so both of these are belt-and-braces against a redelivery that races the commit.
        if (ResolvedByDeadline || PendingServices.Count == 0) return null;

        ResolvedByDeadline = true;

        PrivacySagaTelemetry.ReportDeadlineExceeded(
            logger,
            SagaName,
            Id,
            UserId,
            PendingServices,
            Env.SagaDeadlines.DataExport,
            1,
            "The archive is being assembled WITHOUT these services' sections, each recorded as an "
            + "error in its manifest. Check that each is deployed and that its "
            + "ExportUserDataCommandHandler is registered, then have the subject request a new "
            + "export - this one is incomplete and cannot be topped up.");

        foreach (var service in PendingServices)
        {
            Fragments.Add(MissingFragment(service, Env.SagaDeadlines.DataExport));
        }

        PendingServices.Clear();
        MarkCompleted();

        return new AssembleUserDataExportCommand
        {
            ExportId = Id,
            UserId = UserId,
            Fragments = Fragments,
        };
    }

    /// <summary>The stand-in section for a service that never answered.</summary>
    internal static UserDataExportFragment MissingFragment(string service, TimeSpan deadline) => new()
    {
        Service = service,
        Error = $"The {service} service did not respond within {deadline}. This section of the export is missing.",
        RowCounts = new Dictionary<string, int>(),
        FragmentJson =
            $$"""
              {
                "error": "The {{service}} service did not respond within {{deadline}}; its section of this export could not be produced.",
                "complete": false
              }
              """,
    };
}
