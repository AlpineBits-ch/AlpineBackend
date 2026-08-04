using System.Diagnostics.Metrics;
using AppEnvironment;
using Sentry;

namespace Echo.Sagas;

/// <summary>
/// The operator-facing half of the saga deadlines (item 1 of the privacy workstream's operational
/// gaps): what gets logged, counted and reported when <see cref="AccountDeletionSaga"/> or <see
/// cref="ExportUserDataSaga"/> runs out of patience waiting for a participant.
/// </summary>
public static class PrivacySagaTelemetry
{
    public const string MeterName = "Echo.Sagas.Privacy";

    /// <summary>The counter an operator alerts on.</summary>
    public const string DeadlineExceededCounter = "echo.privacy_saga.deadline_exceeded";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> DeadlineExceeded = Meter.CreateCounter<long>(
        DeadlineExceededCounter,
        unit: "{service}",
        description: "Incremented once per participating service that had not answered a privacy "
                     + "saga by its deadline. Tagged with the saga and the service; carries no "
                     + "account identifiers.");

    /// <summary>
    /// Raises the alarm for one deadline breach.
    /// </summary>
    /// <param name="saga">Stable, low-cardinality saga name used as a metric tag.</param>
    /// <param name="sagaId">The saga's own id - the user id for a purge, the export id for an export.</param>
    /// <param name="subjectUserId">The account whose right is not being honoured.</param>
    /// <param name="missingServices">Exactly who has not answered. The whole diagnostic value is here.</param>
    /// <param name="deadline">The window that elapsed.</param>
    /// <param name="breach">1 for the first breach of this saga, incrementing on each re-armed deadline.</param>
    /// <param name="consequence">What the saga did about it, in operator-readable words.</param>
    public static void ReportDeadlineExceeded(
        ILogger logger,
        string saga,
        string sagaId,
        string subjectUserId,
        IReadOnlyList<string> missingServices,
        TimeSpan deadline,
        int breach,
        string consequence)
    {
        var named = string.Join(", ", missingServices);

        logger.LogError(
            "{Saga} saga {SagaId} for user {UserId} passed its {Deadline} deadline (breach #{Breach}). "
            + "{MissingCount} of the participating services have still not acknowledged: {MissingServices}. "
            + "{Consequence}",
            saga, sagaId, subjectUserId, deadline, breach, missingServices.Count, named, consequence);

        foreach (var service in missingServices)
        {
            DeadlineExceeded.Add(1,
                new KeyValuePair<string, object?>("saga", saga),
                new KeyValuePair<string, object?>("service", service));
        }

        CaptureSentryEvent(saga, sagaId, subjectUserId, missingServices, named, deadline, breach, consequence);
    }

    private static void CaptureSentryEvent(
        string saga,
        string sagaId,
        string subjectUserId,
        IReadOnlyList<string> missingServices,
        string named,
        TimeSpan deadline,
        int breach,
        string consequence)
    {
        try
        {
            var sentryEvent = new SentryEvent
            {
                Level = SentryLevel.Error,
                Message = $"{saga} saga passed its {deadline} deadline; "
                          + $"{missingServices.Count} service(s) have not acknowledged: {named}",
            };

            // Grouped per saga and per missing-service set rather than per subject, so a broker
            // outage that stalls a hundred purges is one issue with a hundred events instead of a
            // hundred issues.
            sentryEvent.SetFingerprint([MeterName, saga, named]);
            sentryEvent.SetTag("saga", saga);
            sentryEvent.SetTag("missing_services", named);
            sentryEvent.SetTag("deadline_breach", breach.ToString());
            sentryEvent.SetExtra("consequence", consequence);

            // Not the message text: see this class's remarks.
            sentryEvent.User.Id = subjectUserId;
            if (!string.Equals(sagaId, subjectUserId, StringComparison.Ordinal))
            {
                sentryEvent.SetTag("saga_id", sagaId);
            }

            SentrySdk.CaptureEvent(sentryEvent);
        }
        catch (Exception)
        {
            // Alerting must never be able to break the saga it is alerting about.
        }
    }
}
