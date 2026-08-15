using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Stripe;

/// <summary>What a delivery said about money changing hands, which is the only thing the dunning
/// clock reacts to. Everything else about a subscription is read back from Stripe rather than
/// inferred from the event.</summary>
public enum StripeDunningSignal
{
    /// <summary>A subscription event. Says nothing about an invoice either way.</summary>
    None,

    /// <summary><c>invoice.payment_failed</c>.</summary>
    PaymentFailed,

    /// <summary><c>invoice.paid</c>.</summary>
    PaymentSucceeded,
}

/// <summary>
/// The part of a reconcile that needs no database: given what Stripe says now, what the grace clock
/// should read and whether the subject keeps the plan.
/// </summary>
public sealed record SubscriptionDecision(
    SubscriptionStatus Status,
    DateTimeOffset? GracePeriodEndsAt,
    bool Downgrade);

/// <summary>What one reconcile concluded.</summary>
public sealed record SubscriptionReconciliation(
    string Outcome,
    IReadOnlyList<EntitlementsChanged> Announcements)
{
    public static SubscriptionReconciliation Nothing(string outcome) => new(outcome, []);
}

/// <summary>
/// Brings one local subscription, and the plan assignment behind it, back in line with what Stripe
/// currently says.
/// </summary>
public sealed class SubscriptionReconciler(
    MicroserviceContext db,
    IStripeGateway gateway,
    StripeCatalogueSync catalogue,
    EntitlementVersionService versions,
    EntitlementPlanOptions plans,
    StripeOptions options,
    TimeProvider clock,
    ILogger<SubscriptionReconciler>? logger = null)
{
    /// <summary>The subject a subscription pays for, written into Stripe metadata at checkout and
    /// read back here. Both directions of identity, section 4: a webhook that cannot work out which
    /// guild it is about is an incident at 2am and the cost of preventing it is a dictionary literal.
    /// </summary>
    public const string SubjectKindMetadataKey = "venta_subject_kind";

    public const string SubjectIdMetadataKey = "venta_subject_id";

    /// <summary>The account being charged.</summary>
    public const string PayerUserIdMetadataKey = "venta_payer_user_id";

    /// <summary>
    /// What the grace clock should read and whether the subject keeps the plan, given Stripe's
    /// current status.
    /// </summary>
    public static SubscriptionDecision Decide(
        SubscriptionStatus status,
        DateTimeOffset? gracePeriodEndsAt,
        StripeDunningSignal signal,
        DateTimeOffset now,
        TimeSpan grace)
    {
        var clockEndsAt = signal switch
        {
            StripeDunningSignal.PaymentSucceeded => null,

            // Not restarted on a repeat failure.
            StripeDunningSignal.PaymentFailed => gracePeriodEndsAt ?? now + grace,

            _ => gracePeriodEndsAt,
        };

        if (status == SubscriptionStatus.PastDue)
        {
            clockEndsAt ??= now + grace;
        }
        else if (signal != StripeDunningSignal.PaymentFailed && SubscriptionStatuses.IsLive(status))
        {
            // Stripe says the subscription is in good standing, so there is nothing outstanding for
            // a clock to be counting down.
            clockEndsAt = null;
        }

        var downgrade = !SubscriptionStatuses.IsLive(status)
                        || (status == SubscriptionStatus.PastDue
                            && clockEndsAt is not null
                            && clockEndsAt <= now);

        return new SubscriptionDecision(status, clockEndsAt, downgrade);
    }

    /// <summary>
    /// Reads the subscription fresh from Stripe and writes whatever it says onto our rows.
    /// </summary>
    /// <param name="stripeSubscriptionId">The only thing taken from the event payload.</param>
    /// <param name="signal">Whether an invoice was paid or failed, which is all the dunning clock
    /// reacts to.</param>
    /// <param name="eventType">Named in the assignment's reason, so the provenance screen can say
    /// which event moved somebody.</param>
    /// <param name="eventAt">Stripe's <c>created</c>, recorded for observability. It orders
    /// nothing.</param>
    /// <param name="cancellationToken">The request's token.</param>
    public async Task<SubscriptionReconciliation> ReconcileAsync(
        string stripeSubscriptionId,
        StripeDunningSignal signal,
        string eventType,
        DateTimeOffset? eventAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeSubscriptionId);

        var now = clock.GetUtcNow();

        var snapshot = await gateway.GetSubscriptionAsync(stripeSubscriptionId, cancellationToken);

        var local = await db.Subscriptions.FirstOrDefaultAsync(
            row => row.StripeSubscriptionId == stripeSubscriptionId, cancellationToken);

        // Null is a real answer rather than a failure: an event can name a subscription that has been
        // deleted since, and the correct response is to reconcile it away.
        if (snapshot is null) return await GoneAsync(local, stripeSubscriptionId, eventType, eventAt, now, cancellationToken);

        var status = SubscriptionStatuses.Parse(snapshot.Status);

        if (status == SubscriptionStatus.Unknown)
        {
            // Not a throw.
            logger?.LogWarning(
                "Stripe reports subscription {Subscription} as '{Status}', which this build does not "
                + "recognise. Treating it as not live; if that status is meant to keep a subject on "
                + "their plan, SubscriptionStatuses needs it.", stripeSubscriptionId, snapshot.Status);
        }

        var reference = snapshot.PriceId is null
            ? null
            : await catalogue.ResolvePriceAsync(snapshot.PriceId, cancellationToken);

        if (local is null)
        {
            var created = await CreateLocalAsync(snapshot, reference, cancellationToken);
            if (created is null)
            {
                return SubscriptionReconciliation.Nothing(
                    $"{eventType}: subscription {stripeSubscriptionId} could not be identified here, "
                    + "so nothing was written.");
            }

            local = created;
        }
        else if (reference is not null)
        {
            local.PlanId = reference.Plan.Id;
            local.VersionNumber = reference.Version.VersionNumber;
        }
        else
        {
            // The price is one we have no row for - a Price created by hand in the dashboard, or a
            // plan version deleted from underneath us.
            logger?.LogWarning(
                "Stripe subscription {Subscription} is priced at {Price}, which no plan version here "
                + "claims. Keeping the pinned plan {Plan} version {Version}.",
                stripeSubscriptionId, snapshot.PriceId, local.PlanId, local.VersionNumber);
        }

        var decision = Decide(status, local.GracePeriodEndsAt, signal, now, options.DunningGrace);

        local.MirrorFromStripe(
            decision.Status,
            snapshot.CurrentPeriodEnd,
            snapshot.CancelAtPeriodEnd,
            snapshot.LatestInvoiceId,
            eventAt);

        local.GracePeriodEndsAt = decision.GracePeriodEndsAt;

        if (!decision.Downgrade)
        {
            var reason = $"Stripe subscription {stripeSubscriptionId} is {snapshot.Status} "
                         + $"({eventType}).";

            var announcement = await AssignAsync(
                local.Subject, local.PlanId, local.VersionNumber, AssignedBy(stripeSubscriptionId),
                reason, now, cancellationToken);

            return new SubscriptionReconciliation(reason, Announcements(announcement));
        }

        var downgrade = await MoveToDefaultPlanAsync(
            local, $"the Stripe subscription is {snapshot.Status} ({eventType})", now, cancellationToken);

        return downgrade;
    }

    /// <summary>Stripe has no such subscription.</summary>
    private async Task<SubscriptionReconciliation> GoneAsync(
        Subscription? local,
        string stripeSubscriptionId,
        string eventType,
        DateTimeOffset? eventAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (local is null)
        {
            return SubscriptionReconciliation.Nothing(
                $"{eventType}: Stripe has no subscription {stripeSubscriptionId} and neither do we.");
        }

        local.MirrorFromStripe(
            SubscriptionStatus.Canceled,
            local.CurrentPeriodEnd,
            local.CancelAtPeriodEnd,
            local.LatestInvoiceId,
            eventAt);

        local.GracePeriodEndsAt = null;

        return await MoveToDefaultPlanAsync(
            local, $"the Stripe subscription no longer exists ({eventType})", now, cancellationToken);
    }

    /// <summary>
    /// First sight of a subscription, which is the normal case for
    /// <c>customer.subscription.created</c> - the webhook usually beats the checkout response back.
    /// </summary>
    private async Task<Subscription?> CreateLocalAsync(
        StripeSubscriptionSnapshot snapshot,
        PlanVersionReference? reference,
        CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            logger?.LogError(
                "Stripe subscription {Subscription} is priced at {Price}, which no plan version here "
                + "claims, and there is no local row to fall back on. Nothing was written: a "
                + "subscription pinned to a plan we cannot name would resolve to nothing.",
                snapshot.Id, snapshot.PriceId);

            return null;
        }

        if (!TryResolveSubject(snapshot, out var subject))
        {
            logger?.LogError(
                "Stripe subscription {Subscription} carries no usable {KindKey}/{IdKey} metadata, so "
                + "there is no way to tell whose plan it pays for. Nothing was written.",
                snapshot.Id, SubjectKindMetadataKey, SubjectIdMetadataKey);

            return null;
        }

        var payer = await ResolvePayerAsync(snapshot, cancellationToken);

        if (payer is null)
        {
            logger?.LogError(
                "Stripe subscription {Subscription} has no StripeCustomer row for {Customer} and no "
                + "{PayerKey} metadata, so there is no account to record as the payer. Nothing was "
                + "written.", snapshot.Id, snapshot.CustomerId, PayerUserIdMetadataKey);

            return null;
        }

        var created = new Subscription
        {
            Id = Subscription.GenerateId(),
            StripeSubscriptionId = snapshot.Id,
            PayerUserId = payer,
            SubjectKind = subject.Kind,
            SubjectId = subject.Id,
            PlanId = reference.Plan.Id,
            VersionNumber = reference.Version.VersionNumber,
            Status = SubscriptionStatus.Incomplete,
        };

        db.Subscriptions.Add(created);

        return created;
    }

    private static bool TryResolveSubject(
        StripeSubscriptionSnapshot snapshot, out EntitlementSubject subject)
    {
        subject = default;

        if (!snapshot.Metadata.TryGetValue(SubjectIdMetadataKey, out var id)
            || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        snapshot.Metadata.TryGetValue(SubjectKindMetadataKey, out var kindText);

        if (!Enum.TryParse<SubjectKind>(kindText, ignoreCase: true, out var kind)) return false;

        subject = new EntitlementSubject(kind, id.Trim());
        return true;
    }

    /// <summary>The <see cref="StripeCustomer"/> row first, because it is what we actually wrote;
    /// metadata second, because it is what a person can repair from the dashboard.</summary>
    private async Task<string?> ResolvePayerAsync(
        StripeSubscriptionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.CustomerId))
        {
            var customerId = snapshot.CustomerId;

            var known = await db.StripeCustomers
                .Where(row => row.StripeCustomerId == customerId)
                .Select(row => row.UserId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(known)) return known;
        }

        return snapshot.Metadata.TryGetValue(PayerUserIdMetadataKey, out var payer)
               && !string.IsNullOrWhiteSpace(payer)
            ? payer.Trim()
            : null;
    }

    /// <summary>
    /// The end of a paid relationship: the instance's default plan, assigned explicitly.
    /// </summary>
    private async Task<SubscriptionReconciliation> MoveToDefaultPlanAsync(
        Subscription local,
        string because,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kind = local.SubjectKind;
        var subjectId = local.SubjectId;

        var current = await db.PlanAssignments.FirstOrDefaultAsync(
            row => row.SubjectKind == kind && row.SubjectId == subjectId, cancellationToken);

        if (current is not null && current.AssignedBy != AssignedBy(local.StripeSubscriptionId))
        {
            logger?.LogInformation(
                "Subscription {Subscription} ended, and {Kind} {Subject} is on an assignment made by "
                + "{AssignedBy} rather than by it. Left alone: this subscription only takes away what "
                + "it gave.", local.StripeSubscriptionId, kind, subjectId, current.AssignedBy);

            return SubscriptionReconciliation.Nothing(
                $"{because}; the subject's plan was assigned by {current.AssignedBy}, not by this "
                + "subscription, so it was left alone.");
        }

        var name = local.SubjectKind == SubjectKind.Guild ? plans.DefaultGuildPlan : plans.DefaultUserPlan;

        if (string.IsNullOrWhiteSpace(name))
        {
            // A deployment that has not named a default plan resolves every key from the catalogue
            // defaults anyway, so leaving the assignment alone is the same outcome as deleting it
            // and keeps the row that says who put them there.
            logger?.LogWarning(
                "Subscription {Subscription} ended and no default {Kind} plan is configured, so the "
                + "existing assignment was left where it is.",
                local.StripeSubscriptionId, local.SubjectKind);

            return SubscriptionReconciliation.Nothing(
                $"{because}; no default plan is configured, so the assignment was left alone.");
        }

        var plan = await db.Plans.FirstOrDefaultAsync(row => row.Name == name, cancellationToken);

        if (plan is null)
        {
            logger?.LogError(
                "Subscription {Subscription} ended and the configured default plan '{Plan}' is not in "
                + "the catalogue, so the existing assignment was left where it is.",
                local.StripeSubscriptionId, name);

            return SubscriptionReconciliation.Nothing(
                $"{because}; the configured default plan '{name}' does not exist, so the assignment "
                + "was left alone.");
        }

        var reason = $"Moved to {plan.Name} because {because}.";

        var announcement = await AssignAsync(
            local.Subject, plan.Id, plan.CurrentVersionNumber, AssignedBy(local.StripeSubscriptionId),
            reason, now, cancellationToken);

        logger?.LogInformation(
            "Subscription {Subscription} moved {Kind} {Subject} to {Plan} version {Version}: {Reason}",
            local.StripeSubscriptionId, local.SubjectKind, local.SubjectId, plan.Name,
            plan.CurrentVersionNumber, reason);

        return new SubscriptionReconciliation(reason, Announcements(announcement));
    }

    /// <summary>Upserts the one assignment row for a subject.</summary>
    private async Task<EntitlementsChanged?> AssignAsync(
        EntitlementSubject subject,
        string planId,
        int versionNumber,
        string assignedBy,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        var assignment = await db.PlanAssignments.FirstOrDefaultAsync(
            row => row.SubjectKind == kind && row.SubjectId == id, cancellationToken);

        if (assignment is not null
            && assignment.PlanId == planId
            && assignment.VersionNumber == versionNumber)
        {
            return null;
        }

        if (assignment is null)
        {
            db.PlanAssignments.Add(new PlanAssignment
            {
                Id = PlanAssignment.GenerateId(),
                SubjectKind = kind,
                SubjectId = id,
                PlanId = planId,
                VersionNumber = versionNumber,
                AssignedBy = assignedBy,
                Reason = reason,
                AssignedAt = now,
            });
        }
        else
        {
            assignment.PlanId = planId;
            assignment.VersionNumber = versionNumber;
            assignment.AssignedBy = assignedBy;
            assignment.Reason = reason;
            assignment.AssignedAt = now;
        }

        return new EntitlementsChanged
        {
            SubjectKind = kind,
            SubjectId = id,
            Reason = EntitlementsChangedReason.PlanAssignmentChanged,

            // Advanced inside the same transaction as the assignment, so a delivery that fails to
            // commit does not leave a client told to refetch something that did not change.
            Version = await versions.AdvanceAsync(subject, cancellationToken),
            ChangedKeys = await ChangedKeysAsync(planId, versionNumber, cancellationToken),
            OccurredAt = now,
        };
    }

    /// <summary>Advisory only, so a version row that cannot be found produces an empty list rather
    /// than a failure - a consumer that ignores this must still be correct.</summary>
    private async Task<List<string>> ChangedKeysAsync(
        string planId, int versionNumber, CancellationToken cancellationToken)
    {
        var json = await db.PlanVersions
            .Where(row => row.PlanId == planId && row.VersionNumber == versionNumber)
            .Select(row => row.ValuesJson)
            .FirstOrDefaultAsync(cancellationToken);

        return json is null ? [] : [.. PlanCatalogueService.ReadValues(json).Keys];
    }

    private static List<EntitlementsChanged> Announcements(EntitlementsChanged? announcement) =>
        announcement is null ? [] : [announcement];

    /// <summary>Section 3's spelling, verbatim.</summary>
    public static string AssignedBy(string stripeSubscriptionId) => $"stripe:{stripeSubscriptionId}";
}
