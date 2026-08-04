using System.Diagnostics.Metrics;
using AppEnvironment;
using Sentry;

namespace Echo.Sagas;

/// <summary>
/// The operator-facing half of the saga deadlines (item 1 of the privacy workstream's operational
/// gaps): what gets logged, counted and reported when <see cref="AccountDeletionSaga"/> or
/// <see cref="ExportUserDataSaga"/> runs out of patience waiting for a participant.
///
/// <para><b>Three channels, because they answer different questions.</b> The <c>Error</c> log names
/// the individual services that did not answer and is what somebody reads while fixing it. The
/// counter is what an alert rule is written against - it carries the saga and the missing service as
/// tags and no identifiers at all, so it is safe to export and low-cardinality. The Sentry event is
/// what actually wakes somebody up, since Sentry is the only alerting sink wired into this
/// deployment today.</para>
///
/// <para><b>The subject id goes to Sentry as a user id, deliberately.</b> Putting it in the message
/// text would smuggle an account identifier past the T0-4 gate; putting it on the event's
/// <c>User</c> means <see cref="SentryPrivacy.Scrub"/> pseudonymizes it exactly like every other
/// event, and the gateway - which has no consent lookup wired - always fails closed. The real id is
/// still in the local log, which is first-party and is where the person fixing it is looking
/// anyway.</para>
/// </summary>
public static class PrivacySagaTelemetry
{
    public const string MeterName = "Echo.Sagas.Privacy";

    /// <summary>The counter an operator alerts on. Any non-zero rate means an erasure or an access
    /// request is not finishing.</summary>
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

            // Not the message text: see this class's remarks. The purge saga's id *is* the subject's
            // user id, so it only ever travels through the field the consent gate scrubs.
            sentryEvent.User.Id = subjectUserId;
            if (!string.Equals(sagaId, subjectUserId, StringComparison.Ordinal))
            {
                sentryEvent.SetTag("saga_id", sagaId);
            }

            SentrySdk.CaptureEvent(sentryEvent);
        }
        catch (Exception)
        {
            // Alerting must never be able to break the saga it is alerting about. The Error log and
            // the counter above have already been emitted at this point.
        }
    }
}
