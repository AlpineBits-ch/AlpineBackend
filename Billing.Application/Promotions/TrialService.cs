using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Promotions;

/// <summary>What starting a trial produced.</summary>
public sealed record TrialStartResult(
    PromotionCampaign Campaign,
    PromotionRedemption Owner,
    SubscriptionDto Subscription,
    string? ClientSecret);

/// <summary>What moving one produced: the guild row that now carries it, and whatever entitlement
/// changes the two guilds have to be told about.</summary>
public sealed record TrialMoveResult(
    PromotionRedemption Guild,
    IReadOnlyList<EntitlementsChanged> Announcements);

/// <summary>Starting a trial, and moving one between guilds.</summary>
public sealed class TrialService(
    MicroserviceContext db,
    PromotionCampaignService campaigns,
    PromotionEligibilityService eligibility,
    PromotionRedemptionService redemptions,
    SubscriptionCheckoutService checkout,
    StripeCustomerRegistry customers,
    PlanService plans,
    IStripeGateway gateway,
    EntitlementPlanOptions options,
    ILogger<TrialService>? logger = null)
{
    /// <summary>Which campaign a Stripe subscription came from, so the dashboard answers "why is this
    /// one free" without anybody opening our console. Read by nothing here, deliberately: the
    /// authoritative record is the <c>PromotionRedemption</c>, and a second copy that something parsed
    /// would be a second source of truth for the one fact this wave exists to keep straight.</summary>
    public const string CampaignMetadataKey = "venta_promotion_campaign";

    /// <summary>
    /// Starts a trial: the card, then the decision, then the subscription, then the permanent record.
    /// </summary>
    /// <param name="campaignCode">The campaign being redeemed, by code or by id.</param>
    /// <param name="guildId">The guild it applies to. Required for a guild campaign, which is every
    /// trial section 7.2 is written about; null for a user-scoped one.</param>
    /// <param name="paymentMethodId">The card the client has just attached, when it knows which. Null
    /// falls back to the account's default card - see
    /// <see cref="IStripeGateway.GetCardIdentityAsync"/>.</param>
    /// <param name="callerUserId">The account taking the offer. The trial attaches here.</param>
    /// <param name="cancellationToken">The request's token.</param>
    public async Task<TrialStartResult> StartAsync(
        string campaignCode,
        string? guildId,
        string? paymentMethodId,
        string callerUserId,
        CancellationToken cancellationToken)
    {
        RequireStripe();
        ArgumentException.ThrowIfNullOrWhiteSpace(callerUserId);

        var campaign = await campaigns.RequireAsync(campaignCode, cancellationToken);
        var subject = await ResolveSubjectAsync(campaign, guildId, callerUserId, cancellationToken);

        var (plan, version) = await checkout.RequirePurchasableAsync(
            campaign.Plan, subject.Kind, cancellationToken);

        // ── The card, before the decision ────────────────────────────────────
        var customer = await customers.FindAsync(callerUserId, cancellationToken);

        var card = customer is null
            ? null
            : await gateway.GetCardIdentityAsync(
                customer.StripeCustomerId, paymentMethodId, cancellationToken);

        var decision = await eligibility.EvaluateAsync(
            campaign,
            callerUserId,
            subject.Kind == SubjectKind.Guild ? subject.Id : null,
            new PromotionCardEvidence(card is not null, card?.Fingerprint),
            cancellationToken);

        if (!decision.Allowed) throw decision.ToRefusal();

        // Deliberately after the eligibility decision rather than before it.
        if (await checkout.LiveSubscriptionForAsync(subject, cancellationToken) is not null)
        {
            throw new CheckoutRefusedException(
                BillingErrorCodes.AlreadySubscribed, StatusCodes.Status409Conflict,
                "This already has a live subscription, so there is nothing a trial would add. Cancel it "
                + "first if the trial is what was wanted.");
        }

        // The slot is taken before anything is created, not after. Charging once the Stripe
        // subscription exists left the cap enforced against a subscription no refusal could undo,
        // because the endpoint catches this and returns normally.
        campaigns.Charge(campaign, logger);

        // ── Only now is anything created ─────────────────────────────────────

        var stripeCustomer = customer ?? await customers.EnsureAsync(
            callerUserId, email: null, address: null, cancellationToken);

        var metadata = SubscriptionCheckoutService.MetadataFor(subject, plan, version, callerUserId);
        metadata[CampaignMetadataKey] = campaign.Code;

        StripeSubscriptionResult result;
        try
        {
            result = await checkout.CreateTrialWithTaxFallbackAsync(
                stripeCustomer.StripeCustomerId,
                version.StripePriceId!,
                subject,
                metadata,
                campaign.TrialDays,
                card?.PaymentMethodId,
                campaign.Code,
                cancellationToken);
        }
        catch
        {
            campaigns.Release(campaign);
            throw;
        }

        var row = new Subscription
        {
            Id = Subscription.GenerateId(),
            StripeSubscriptionId = result.Subscription.Id,
            PayerUserId = callerUserId,
            SubjectKind = subject.Kind,
            SubjectId = subject.Id,
            PlanId = plan.Id,
            VersionNumber = version.VersionNumber,
            Status = SubscriptionStatuses.Parse(result.Subscription.Status),
            CurrentPeriodEnd = result.Subscription.CurrentPeriodEnd,
            CancelAtPeriodEnd = result.Subscription.CancelAtPeriodEnd,
            LatestInvoiceId = result.Subscription.LatestInvoiceId,
        };

        db.Subscriptions.Add(row);

        // The permanent record, and the marks that will refuse the next account presenting the same
        // device, number or card.
        var recorded = await redemptions.RecordAsync(
            campaign,
            callerUserId,
            subject.Kind == SubjectKind.Guild ? subject.Id : null,
            decision.Hashes,
            result.Subscription.Id,
            cancellationToken);

        logger?.LogInformation(
            "Account {Owner} started '{Campaign}' on {SubjectKind} {SubjectId} as Stripe subscription "
            + "{Subscription}, {Days} days, with {Card} on file. The plan itself is switched on by the "
            + "webhook, not here.",
            callerUserId, campaign.Code, subject.Kind, subject.Id, result.Subscription.Id,
            campaign.TrialDays, card is null ? "no card" : "a card");

        return new TrialStartResult(
            campaign,
            recorded.Owner,
            SubscriptionDto.From(row, plan, version, isPayer: true),
            result.ClientSecret);
    }

    /// <summary>Moves a live trial onto another guild, keeping the clock.</summary>
    public async Task<TrialMoveResult> MoveAsync(
        string campaignCode,
        string toGuildId,
        string callerUserId,
        CancellationToken cancellationToken)
    {
        RequireStripe();
        ArgumentException.ThrowIfNullOrWhiteSpace(callerUserId);

        var campaign = await campaigns.RequireAsync(campaignCode, cancellationToken);

        if (campaign.SubjectKind != SubjectKind.Guild)
        {
            throw new PromotionRefusedException(PromotionErrorCodes.TargetRequired,
                $"'{campaign.Code}' applies to the account rather than to a server, so there is nothing "
                + "to move it between.");
        }

        var target = await ResolveSubjectAsync(campaign, toGuildId, callerUserId, cancellationToken);

        var owner = await redemptions.FindOwnerRedemptionAsync(
            campaign.Id, callerUserId, cancellationToken);

        // Something else paying for that server would leave two things buying the same plan.
        var occupied = await checkout.LiveSubscriptionForAsync(target, cancellationToken);

        if (occupied is not null && occupied.StripeSubscriptionId != owner?.StripeSubscriptionId)
        {
            throw new CheckoutRefusedException(
                BillingErrorCodes.AlreadySubscribed, StatusCodes.Status409Conflict,
                "That server already has a live subscription of its own. Moving a trial onto it would "
                + "leave two things paying for the same plan.");
        }

        // Read before the move, because the move is what releases it.
        var from = owner is null
            ? null
            : await LiveGuildRowAsync(campaign.Id, callerUserId, cancellationToken);

        var moved = await redemptions.MoveGuildAsync(
            campaign, callerUserId, target.Id, cancellationToken);

        // Already there. A double-clicked button is not an error and nothing needs reassigning.
        if (from is not null && from.SubjectId == moved.SubjectId)
        {
            return new TrialMoveResult(moved, []);
        }

        var subscription = owner?.StripeSubscriptionId is { Length: > 0 } stripeId
            ? await checkout.FindAsync(stripeId, cancellationToken)
            : null;

        if (subscription is null)
        {
            // A campaign that confers a plan with no Stripe subscription behind it, or a row that
            // has been reconciled away.
            logger?.LogWarning(
                "Account {Owner} moved their '{Campaign}' trial to {Guild}, but no subscription row "
                + "backs it, so only the redemption moved.", callerUserId, campaign.Code, target.Id);

            return new TrialMoveResult(moved, []);
        }

        var plan = await db.Plans.FirstOrDefaultAsync(
            row => row.Id == subscription.PlanId, cancellationToken);

        var version = await db.PlanVersions.FirstOrDefaultAsync(
            row => row.PlanId == subscription.PlanId
                   && row.VersionNumber == subscription.VersionNumber,
            cancellationToken);

        if (plan is null || version is null)
        {
            // The subscription is pinned to something this catalogue can no longer name, which the
            // Restrict foreign key is supposed to make impossible.
            logger?.LogError(
                "Subscription {Subscription} is pinned to plan {Plan} version {Version}, which does "
                + "not resolve, so '{Campaign}' moved on paper only.",
                subscription.StripeSubscriptionId, subscription.PlanId, subscription.VersionNumber,
                campaign.Code);

            return new TrialMoveResult(moved, []);
        }

        var announcements = new List<EntitlementsChanged>();

        EntitlementSubject? left = from is null
            ? null
            : new EntitlementSubject(SubjectKind.Guild, from.SubjectId);

        subscription.SubjectKind = target.Kind;
        subscription.SubjectId = target.Id;

        // Stripe's copy of "whose plan is this" has to move too.
        var metadata = SubscriptionCheckoutService.MetadataFor(
            target, plan, version, subscription.PayerUserId);

        metadata[CampaignMetadataKey] = campaign.Code;

        await gateway.UpdateSubscriptionMetadataAsync(
            subscription.StripeSubscriptionId,
            metadata,
            StripeIdempotencyKey.ForRequest("trial_move", subscription.StripeSubscriptionId),
            cancellationToken);

        var actor = SubscriptionReconciler.AssignedBy(subscription.StripeSubscriptionId);

        await AssignAsync(
            target,
            plan.Name,
            subscription.VersionNumber,
            $"'{campaign.Code}' was moved here by {callerUserId}.",
            actor,
            announcements,
            cancellationToken);

        if (left is { } vacated)
        {
            await ReleaseAsync(vacated, campaign, actor, announcements, cancellationToken);
        }

        logger?.LogInformation(
            "Account {Owner} moved their '{Campaign}' trial from {From} to {To}. It still ends "
            + "{EndsAt}, which is the date it was always going to end.",
            callerUserId, campaign.Code, left?.Id ?? "nowhere", target.Id,
            moved.EndsAt?.ToString("O") ?? "never");

        return new TrialMoveResult(moved, announcements);
    }

    /// <summary>Whether this account could take this campaign right now.</summary>
    public Task<bool> IsEligibleAsync(
        string campaignCode, string? guildId, string callerUserId, CancellationToken cancellationToken) =>
        eligibility.IsEligibleAsync(campaignCode, callerUserId, guildId, cancellationToken);

    /// <summary>
    /// The subject a redemption applies to, having established the caller may put a plan on it.
    /// </summary>
    private async Task<EntitlementSubject> ResolveSubjectAsync(
        PromotionCampaign campaign,
        string? guildId,
        string callerUserId,
        CancellationToken cancellationToken)
    {
        if (campaign.SubjectKind != SubjectKind.Guild)
        {
            return new EntitlementSubject(SubjectKind.User, callerUserId);
        }

        if (string.IsNullOrWhiteSpace(guildId))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.TargetRequired,
                $"'{campaign.Code}' applies to a server, so it needs one naming.");
        }

        var id = guildId.Trim();

        if (!await checkout.CanManageGuildAsync(id, callerUserId, cancellationToken))
        {
            throw new PromotionRefusedException(PromotionErrorCodes.TargetRequired,
                "Managing the server is what it takes to put it on a trial.");
        }

        return new EntitlementSubject(SubjectKind.Guild, id);
    }

    /// <summary>The guild row currently carrying this account's trial, or null.</summary>
    private Task<PromotionRedemption?> LiveGuildRowAsync(
        string campaignId, string ownerUserId, CancellationToken cancellationToken) =>
        db.PromotionRedemptions.FirstOrDefaultAsync(
            row => row.CampaignId == campaignId
                   && row.SubjectKind == SubjectKind.Guild
                   && row.OwnerUserId == ownerUserId
                   && row.ReleasedAt == null,
            cancellationToken);

    /// <summary>Puts the guild the trial left back on the instance's default plan, but only if this
    /// subscription is what put it where it is. An assignment somebody else made - a staff grant, an
    /// onboarding agreement, another promotion - is left exactly where it is, which is the same rule
    /// <c>SubscriptionReconciler.MoveToDefaultPlanAsync</c> follows and for the same reason.</summary>
    private async Task ReleaseAsync(
        EntitlementSubject subject,
        PromotionCampaign campaign,
        string actor,
        List<EntitlementsChanged> announcements,
        CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        var current = await db.PlanAssignments.FirstOrDefaultAsync(
            row => row.SubjectKind == kind && row.SubjectId == id, cancellationToken);

        if (current is null || current.AssignedBy != actor) return;

        if (string.IsNullOrWhiteSpace(options.DefaultGuildPlan))
        {
            logger?.LogWarning(
                "'{Campaign}' moved off {Guild} and no default guild plan is configured, so the "
                + "assignment it made was left where it is.", campaign.Code, id);

            return;
        }

        await AssignAsync(
            subject,
            options.DefaultGuildPlan,
            versionNumber: null,
            $"'{campaign.Code}' moved to another server.",
            actor,
            announcements,
            cancellationToken);
    }

    /// <summary>
    /// One assignment, through the same service the staff console assigns with, so a trial move is
    /// audited exactly as a hand-made assignment is.
    /// </summary>
    private async Task AssignAsync(
        EntitlementSubject subject,
        string planName,
        int? versionNumber,
        string reason,
        string actor,
        List<EntitlementsChanged> announcements,
        CancellationToken cancellationToken)
    {
        try
        {
            var (_, announcement) = await plans.AssignAsync(
                subject, planName, versionNumber, reason, actor, cancellationToken);

            announcements.Add(announcement);
        }
        catch (PlanRefusedException refusal)
        {
            logger?.LogError(refusal,
                "A trial move could not put {Kind} {Subject} on '{Plan}' ({Code}), so their plan was "
                + "left alone. The redemption record is still correct.",
                subject.Kind, subject.Id, planName, refusal.Code);
        }
    }

    private static void RequireStripe()
    {
        if (!Env.License.IsStripeConfigured) throw CheckoutRefusedException.BillingDisabled();
    }
}
