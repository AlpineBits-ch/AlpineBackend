using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Echo.Sagas;

/// <summary>
/// The export saga's deadline, and the read-side twin of <see cref="AccountPurgeDeadlineElapsed"/>.
/// Derives from <see cref="TimeoutMessage"/> so Wolverine schedules it and drops it if the saga has
/// already finished.
/// </summary>
public record DataExportDeadlineElapsed() : TimeoutMessage(Env.SagaDeadlines.DataExport)
{
    /// <summary>Also the saga id.</summary>
    public string ExportId { get; set; } = null!;
}

/// <summary>
/// Orchestrates the cross-service data export (GDPR Art. 15 / 20, T1-7 of docs/specs/privacy.md).
/// The read-side sibling of <see cref="AccountDeletionSaga"/>, and deliberately the same shape:
/// starts on DataExportRequestedEvent (published by Identity's DataExportController once the
/// DataExportRequest row is committed), fans ExportUserDataCommand out to every participating
/// service in one publish (each service has its own handler bound to its own queue via Wolverine's
/// conventional routing, so a single publish reaches all of them), and completes once every one of
/// them has answered.
///
/// <para><b>The one structural difference from the deletion saga is the ending.</b> A purge's
/// participants have nothing to hand back - the acknowledgement is the result - so that saga ends by
/// publishing a completion event. An export's participants each produce a JSON fragment, and those
/// fragments have to become a file with an owner and a lifetime. So this one ends by sending
/// AssembleUserDataExportCommand to Identity, which owns the row and the artifact bucket. The
/// gateway orchestrates; it never holds anybody's data at rest.</para>
///
/// <para><b>Fragments are carried in saga state.</b> That is the simple thing and it has a ceiling:
/// the whole archive's contents are serialized into the saga row until the last participant answers.
/// It is bounded rather than unbounded - Env.DataExport.MaxMessagesPerConversation caps the largest
/// contributor by far, and a truncated fragment says so explicitly rather than pretending to be
/// complete. If that ceiling ever becomes the binding constraint, the shape that replaces it is each
/// service uploading its own fragment and returning a key; that is a bigger change than it looks,
/// because six of the eight participants have no bucket credentials today.</para>
///
/// <para><b>The deadline resolves the request instead of leaving it Running - the opposite of what
/// the purge saga does, on purpose.</b> A <c>DataExportRequest</c> that sits at <c>Running</c>
/// forever is the worst outcome available here: the subject watches a spinner, the 24-hour rate
/// limit stops them asking again, and the statutory month runs out with nothing to show. Unlike a
/// half-finished erasure, a half-finished export makes no false claim about the world - it is a
/// disclosure that is missing sections, and a disclosure can say so. So when
/// <see cref="DataExportDeadlineElapsed"/> arrives with services outstanding, this saga writes an
/// explicit error fragment for each one that did not answer and assembles anyway: the archive's
/// manifest then names each missing service and why its section is absent, which is a truthful
/// partial answer under Art. 15 rather than a silent one.</para>
///
/// <para><b>How that ending is reported, and why this saga needs no extra contract to say it.</b>
/// A deadline-resolved export is <b>not</b> <c>Ready</c>: Identity's
/// <c>AssembleUserDataExportCommandHandler</c> reads the fragments it is handed, and any fragment
/// carrying an <c>Error</c> - which is exactly what <see cref="MissingFragment"/> writes - marks the
/// <c>DataExportRequest</c> <c>Partial</c> with those services named on the row and in the API
/// response. The archive is still uploaded and still downloadable, because it is the subject's data;
/// only the claim made about it changes from "here is everything" to "here is everything except
/// these". <c>AssembleUserDataExportCommand</c> therefore remains the single lever this saga holds,
/// and it did not need a new field - "which sections are absent" was already in the fragments. The
/// same derivation also catches a participant that answered <i>with</i> an error, which no signal
/// from this saga would have covered.</para>
///
/// <para><b>Known harness gap, not to be worked around here.</b> Echo.E2E.Tests' EchoTestStack cannot
/// spawn Bots or Isle - both inherit TypeLoadMode.Static from the shared ConfigureWolverine and lack
/// the dynamic-codegen fallback the other six services' Program.cs enables under IsDevelopment()
/// (see that class's own remarks, where the deletion saga hit exactly this). Both are named below,
/// so an export started inside that harness fans out to two services that are not running: with the
/// deadline in place that export now resolves - loudly, and with two error fragments naming exactly
/// which services were absent - instead of hanging forever. It is still the harness that has to
/// change, not this list.</para>
/// </summary>
public class ExportUserDataSaga : Saga
{
    private const string SagaName = "data-export";

    /// <summary>
    /// Every service that holds data belonging to an account.
    ///
    /// <para>Identical to <c>AccountDeletionSaga.ParticipatingServices</c>, including Bots and Isle,
    /// and that is the point: the set of services that must forget a person and the set that must be
    /// able to show them what is held about them are the same set. If the two lists ever diverge,
    /// one of them is wrong - either something is being erased that was never disclosed, or something
    /// is being disclosed that erasure will miss.</para>
    /// </summary>
    internal static readonly string[] ParticipatingServices =
        ["identity", "social", "guild", "messaging", "federation", "import", "bots", "isle"];

    /// <summary>The export id, so two exports for different accounts - or a second export for the
    /// same account - never share a saga.</summary>
    public string Id { get; set; }

    public string UserId { get; set; } = null!;

    public List<string> PendingServices { get; set; } = new();

    public List<UserDataExportFragment> Fragments { get; set; } = new();

    /// <summary>Set the moment the deadline resolves this export. Only meaningful in the window
    /// between that decision and the saga row being deleted, which is exactly the window a
    /// redelivered timeout could land in.</summary>
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

    /// <summary>
    /// The deadline. Names the services that never answered - in the alert and in the archive - and
    /// assembles what there is, rather than leaving the subject's request pending forever. See this
    /// class's remarks for why this ending differs from the purge saga's.
    /// </summary>
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

    /// <summary>
    /// The stand-in section for a service that never answered. Written into the archive rather than
    /// omitted from it: a subject comparing the manifest against what they know about the product
    /// must be able to tell "this service holds nothing about me" from "this service did not
    /// answer", and an absent file says the first while meaning the second.
    /// </summary>
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
