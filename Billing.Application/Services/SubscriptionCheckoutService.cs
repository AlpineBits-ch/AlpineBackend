using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Billing.Application.Services;

/// <summary>A refusal with a machine-readable code and the status it is served as.</summary>
public sealed class CheckoutRefusedException(string code, int statusCode, string message)
    : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public static CheckoutRefusedException BillingDisabled() =>
        new(BillingErrorCodes.BillingDisabled, StatusCodes.Status404NotFound,
            "This instance has no Stripe key configured, so nothing is for sale here. The catalogue "
            + "reports the same thing as 'enabled: false'.");

    public static CheckoutRefusedException NotPermitted(string message) =>
        new(BillingErrorCodes.NotPermitted, StatusCodes.Status403Forbidden, message);

    public static CheckoutRefusedException NotThePayer() =>
        new(BillingErrorCodes.NotThePayer, StatusCodes.Status403Forbidden,
            "Somebody else's card is behind this subscription. Only the account paying for it can "
            + "change or cancel it.");

    public static CheckoutRefusedException UnknownSubscription() =>
        new(BillingErrorCodes.UnknownSubscription, StatusCodes.Status404NotFound,
            "No subscription with that id.");
}

/// <summary>Buying, reading, cancelling, resuming and changing a subscription.</summary>
public sealed class SubscriptionCheckoutService(
    MicroserviceContext db,
    IStripeGateway gateway,
    StripeCustomerRegistry customers,
    EntitlementPlanOptions options,
    IMessageBus bus,
    ILogger<SubscriptionCheckoutService>? logger = null)
{
    // ── Create ───────────────────────────────────────────────────────────────

    public async Task<CreateSubscriptionResponse> CreateAsync(
        CreateSubscriptionRequest request, string callerUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireStripe();

        var subject = new EntitlementSubject(request.SubjectKind, request.SubjectId?.Trim() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(subject.Id))
        {
            throw new CheckoutRefusedException(
                BillingErrorCodes.NotPurchasable, StatusCodes.Status400BadRequest,
                "A subscription needs something to be for: a guild id for a guild plan, or the "
                + "account's own id for a user plan.");
        }

        await RequireControlOfAsync(subject, callerUserId, cancellationToken);

        var (plan, version) = await RequirePurchasableAsync(request.PlanName, subject.Kind, cancellationToken);

        await RefuseIfAlreadyLiveAsync(subject, cancellationToken);

        var resumed = await ResumeOrRetireIncompleteAsync(
            subject, plan, version, callerUserId, cancellationToken);

        if (resumed is not null) return resumed;

        var address = request.Address is null
            ? null
            : new StripeAddress(
                request.Address.Country,
                request.Address.Line1,
                request.Address.Line2,
                request.Address.City,
                request.Address.State,
                request.Address.PostalCode);

        var customer = await customers.EnsureAsync(
            callerUserId, request.Email, address, cancellationToken);

        var metadata = MetadataFor(subject, plan, version, callerUserId);

        var result = await CreateWithTaxFallbackAsync(
            customer.StripeCustomerId, version.StripePriceId!, subject, metadata, cancellationToken);

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

        logger?.LogInformation(
            "{Payer} started subscription {Subscription} for {SubjectKind} {SubjectId} on plan "
            + "'{Plan}' version {Version}. It is not live until the webhook says so.",
            callerUserId, row.StripeSubscriptionId, subject.Kind, subject.Id, plan.Name,
            version.VersionNumber);

        return new CreateSubscriptionResponse(
            SubscriptionDto.From(row, plan, version, isPayer: true), result.ClientSecret);
    }

    /// <summary>The Stripe metadata that says whose subscription this is.</summary>
    public static Dictionary<string, string> MetadataFor(
        EntitlementSubject subject, Plan plan, PlanVersion version, string payerUserId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(version);

        return new Dictionary<string, string>
        {
            [SubscriptionReconciler.SubjectKindMetadataKey] = EntitlementSubjectKinds.Of(subject.Kind),
            [SubscriptionReconciler.SubjectIdMetadataKey] = subject.Id,
            [SubscriptionReconciler.PayerUserIdMetadataKey] = payerUserId,
            [StripeCatalogueSync.PlanMetadataKey] = plan.Name,
            [StripeCatalogueSync.PlanVersionMetadataKey] = version.VersionNumber.ToString(),
        };
    }

    /// <summary>Refuses a subject that is already paying for something.</summary>
    private async Task RefuseIfAlreadyLiveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        if (await LiveSubscriptionForAsync(subject, cancellationToken) is null) return;

        throw new CheckoutRefusedException(
            BillingErrorCodes.AlreadySubscribed, StatusCodes.Status409Conflict,
            "This already has a live subscription. Change the plan on it rather than starting a "
            + "second one, which would charge twice for the same thing.");
    }

    /// <summary>
    /// Reopening an abandoned checkout: resume the incomplete attempt, or retire it.
    /// </summary>
    private async Task<CreateSubscriptionResponse?> ResumeOrRetireIncompleteAsync(
        EntitlementSubject subject,
        Plan plan,
        PlanVersion version,
        string callerUserId,
        CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        var pending = await db.Subscriptions
            .Where(subscription => subscription.SubjectKind == kind
                                   && subscription.SubjectId == id
                                   && subscription.Status == SubscriptionStatus.Incomplete)
            .OrderByDescending(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var row in pending)
        {
            var current = await gateway.GetSubscriptionWithSecretAsync(
                row.StripeSubscriptionId, cancellationToken);

            if (current is null)
            {
                // Gone from Stripe entirely.
                row.Status = SubscriptionStatus.Canceled;
                continue;
            }

            Mirror(row, current.Subscription);

            if (row.Status == SubscriptionStatus.Incomplete
                && row.PlanId == plan.Id
                && row.VersionNumber == version.VersionNumber
                && row.PayerUserId == callerUserId)
            {
                logger?.LogInformation(
                    "Reopened checkout resumed subscription {Subscription} for {SubjectKind} "
                    + "{SubjectId} rather than starting a second one.",
                    row.StripeSubscriptionId, subject.Kind, subject.Id);

                return new CreateSubscriptionResponse(
                    SubscriptionDto.From(row, plan, version, isPayer: true), current.ClientSecret);
            }

            if (row.Status == SubscriptionStatus.Incomplete)
            {
                await gateway.CancelIncompleteSubscriptionAsync(
                    row.StripeSubscriptionId,
                    StripeIdempotencyKey.ForRequest("abandon", row.StripeSubscriptionId),
                    cancellationToken);

                row.Status = SubscriptionStatus.Canceled;

                logger?.LogInformation(
                    "Cancelled abandoned subscription {Subscription} for {SubjectKind} {SubjectId}: "
                    + "the customer is now buying something else, and an incomplete attempt left to "
                    + "expire is a client secret that stays confirmable for a day.",
                    row.StripeSubscriptionId, subject.Kind, subject.Id);

                continue;
            }

            // Stripe says it went live while our row said incomplete - a webhook we have not
            // processed yet.
            if (row.IsLive)
            {
                throw new CheckoutRefusedException(
                    BillingErrorCodes.AlreadySubscribed, StatusCodes.Status409Conflict,
                    "This already has a live subscription. Change the plan on it rather than starting "
                    + "a second one, which would charge twice for the same thing.");
            }
        }

        return null;
    }

    /// <summary>
    /// Creates with automatic tax, and once without it if that is what Stripe refused over.
    /// </summary>
    public Task<StripeSubscriptionResult> CreateWithTaxFallbackAsync(
        string customerId,
        string priceId,
        EntitlementSubject subject,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var kind = EntitlementSubjectKinds.Of(subject.Kind);

        return CreateWithTaxFallbackAsync(
            customerId,
            priceId,
            subject,
            metadata,
            trialPeriodDays: null,
            defaultPaymentMethodId: null,
            automaticTax => StripeIdempotencyKey.ForSubscription(kind, subject.Id, priceId, automaticTax),
            cancellationToken);
    }

    /// <summary>The same create and the same fallback, for a trial.</summary>
    public Task<StripeSubscriptionResult> CreateTrialWithTaxFallbackAsync(
        string customerId,
        string priceId,
        EntitlementSubject subject,
        IReadOnlyDictionary<string, string> metadata,
        int trialPeriodDays,
        string? defaultPaymentMethodId,
        string campaignCode,
        CancellationToken cancellationToken)
    {
        var kind = EntitlementSubjectKinds.Of(subject.Kind);

        return CreateWithTaxFallbackAsync(
            customerId,
            priceId,
            subject,
            metadata,
            trialPeriodDays,
            defaultPaymentMethodId,
            automaticTax => StripeIdempotencyKey.ForTrial(kind, subject.Id, campaignCode, automaticTax),
            cancellationToken);
    }

    /// <summary>The one body both creates share.</summary>
    private async Task<StripeSubscriptionResult> CreateWithTaxFallbackAsync(
        string customerId,
        string priceId,
        EntitlementSubject subject,
        IReadOnlyDictionary<string, string> metadata,
        int? trialPeriodDays,
        string? defaultPaymentMethodId,
        Func<bool, StripeIdempotencyKey> key,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.CreateSubscriptionAsync(
                new StripeSubscriptionRequest(
                    customerId, priceId, AutomaticTax: true, metadata, trialPeriodDays,
                    defaultPaymentMethodId),
                key(true),
                cancellationToken);
        }
        catch (StripeGatewayException failure) when (failure.IsTaxFailure)
        {
            logger?.LogWarning(failure,
                "Stripe would not compute tax for {SubjectKind} {SubjectId} (code {Code}), so the "
                + "subscription was created without automatic tax. The sale is not worth losing over "
                + "it; the Stripe Tax registration for this jurisdiction is what needs attention.",
                subject.Kind, subject.Id, failure.StripeCode);

            return await gateway.CreateSubscriptionAsync(
                new StripeSubscriptionRequest(
                    customerId, priceId, AutomaticTax: false, metadata, trialPeriodDays,
                    defaultPaymentMethodId),
                key(false),
                cancellationToken);
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything this caller pays for, plus everything on a guild they can manage.
    /// </summary>
    public async Task<IReadOnlyList<SubscriptionDto>> ListAsync(
        string callerUserId, CancellationToken cancellationToken)
    {
        var manageable = await ManageableGuildIdsAsync(callerUserId, cancellationToken);

        var rows = await db.Subscriptions
            .Where(subscription => subscription.PayerUserId == callerUserId
                                   || (subscription.SubjectKind == SubjectKind.Guild
                                       && manageable.Contains(subscription.SubjectId)))
            .OrderByDescending(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken);

        var dtos = new List<SubscriptionDto>(rows.Count);

        foreach (var row in rows)
        {
            dtos.Add(await ToDtoAsync(row, row.PayerUserId == callerUserId, cancellationToken));
        }

        return dtos;
    }

    public async Task<SubscriptionDto> GetAsync(
        string stripeSubscriptionId, string callerUserId, CancellationToken cancellationToken)
    {
        var row = await FindAsync(stripeSubscriptionId, cancellationToken)
                  ?? throw CheckoutRefusedException.UnknownSubscription();

        var isPayer = row.PayerUserId == callerUserId;

        if (!isPayer)
        {
            // Not "you may not see this": a 404 either way, so this endpoint cannot be walked to
            // find out which subscription ids exist.
            if (row.SubjectKind != SubjectKind.Guild
                || !await CanManageGuildAsync(row.SubjectId, callerUserId, cancellationToken))
            {
                throw CheckoutRefusedException.UnknownSubscription();
            }
        }

        return await ToDtoAsync(row, isPayer, cancellationToken);
    }

    // ── Cancel, resume, change ───────────────────────────────────────────────

    public async Task<SubscriptionDto> CancelAsync(
        string stripeSubscriptionId, string callerUserId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var row = await RequirePayerAsync(stripeSubscriptionId, callerUserId, cancellationToken);

        var snapshot = await gateway.SetCancelAtPeriodEndAsync(
            row.StripeSubscriptionId,
            cancelAtPeriodEnd: true,
            StripeIdempotencyKey.ForRequest("cancel", row.StripeSubscriptionId),
            cancellationToken);

        Mirror(row, snapshot);

        logger?.LogInformation(
            "{Payer} scheduled subscription {Subscription} to end at {PeriodEnd}.",
            callerUserId, row.StripeSubscriptionId, row.CurrentPeriodEnd);

        return await ToDtoAsync(row, isPayer: true, cancellationToken);
    }

    public async Task<SubscriptionDto> ResumeAsync(
        string stripeSubscriptionId, string callerUserId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var row = await RequirePayerAsync(stripeSubscriptionId, callerUserId, cancellationToken);

        // Resuming is clearing a flag on something that is still running.
        if (!row.IsLive)
        {
            throw new CheckoutRefusedException(
                BillingErrorCodes.SubscriptionLapsed, StatusCodes.Status409Conflict,
                "This subscription has already ended, so there is nothing to resume. Starting a new "
                + "one is what brings the plan back.");
        }

        var snapshot = await gateway.SetCancelAtPeriodEndAsync(
            row.StripeSubscriptionId,
            cancelAtPeriodEnd: false,
            StripeIdempotencyKey.ForRequest("resume", row.StripeSubscriptionId),
            cancellationToken);

        Mirror(row, snapshot);

        return await ToDtoAsync(row, isPayer: true, cancellationToken);
    }

    public async Task<ProrationPreviewDto> PreviewChangeAsync(
        string stripeSubscriptionId,
        string planName,
        string callerUserId,
        CancellationToken cancellationToken)
    {
        RequireStripe();

        var row = await RequirePayerAsync(stripeSubscriptionId, callerUserId, cancellationToken);
        var (_, version) = await RequirePurchasableAsync(planName, row.SubjectKind, cancellationToken);

        var preview = await gateway.PreviewSubscriptionPriceChangeAsync(
            row.StripeSubscriptionId, version.StripePriceId!, cancellationToken);

        return new ProrationPreviewDto(
            preview.ImmediateChargeMinorUnits,
            preview.Currency,
            preview.NextInvoiceTotalMinorUnits,
            preview.NextInvoiceAt,
            preview.Lines
                .Select(line => new ProrationLineDto(line.Description, line.AmountMinorUnits))
                .ToList());
    }

    public async Task<SubscriptionDto> ChangeAsync(
        string stripeSubscriptionId,
        string planName,
        string callerUserId,
        CancellationToken cancellationToken)
    {
        RequireStripe();

        var row = await RequirePayerAsync(stripeSubscriptionId, callerUserId, cancellationToken);
        var (plan, version) = await RequirePurchasableAsync(planName, row.SubjectKind, cancellationToken);

        var snapshot = await gateway.ChangeSubscriptionPriceAsync(
            row.StripeSubscriptionId,
            version.StripePriceId!,
            StripeIdempotencyKey.ForRequest("change", row.StripeSubscriptionId),
            cancellationToken);

        // The pinned version moves with the price, because the pair is what says which numbers were
        // bought. The entitlements themselves still move only when the webhook writes the assignment.
        row.PlanId = plan.Id;
        row.VersionNumber = version.VersionNumber;
        Mirror(row, snapshot);

        logger?.LogInformation(
            "{Payer} moved subscription {Subscription} onto plan '{Plan}' version {Version}.",
            callerUserId, row.StripeSubscriptionId, plan.Name, version.VersionNumber);

        return SubscriptionDto.From(row, plan, version, isPayer: true);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static void RequireStripe()
    {
        if (!Env.License.IsStripeConfigured) throw CheckoutRefusedException.BillingDisabled();
    }

    public Task<Subscription?> FindAsync(string stripeSubscriptionId, CancellationToken cancellationToken) =>
        db.Subscriptions.FirstOrDefaultAsync(
            subscription => subscription.StripeSubscriptionId == stripeSubscriptionId, cancellationToken);

    private async Task<Subscription> RequirePayerAsync(
        string stripeSubscriptionId, string callerUserId, CancellationToken cancellationToken)
    {
        var row = await FindAsync(stripeSubscriptionId, cancellationToken)
                  ?? throw CheckoutRefusedException.UnknownSubscription();

        if (row.PayerUserId == callerUserId) return row;

        // Managing the guild is enough to look and never enough to spend or stop somebody else's
        // money, so the two are distinguishable here: a manager gets not_the_payer and a stranger
        // gets the same 404 a nonexistent id gets.
        if (row.SubjectKind == SubjectKind.Guild
            && await CanManageGuildAsync(row.SubjectId, callerUserId, cancellationToken))
        {
            throw CheckoutRefusedException.NotThePayer();
        }

        throw CheckoutRefusedException.UnknownSubscription();
    }

    /// <summary>The live subscription on a subject, or null.</summary>
    public Task<Subscription?> LiveSubscriptionForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        // The same three statuses the filtered unique index is built from.
        var live = SubscriptionStatuses.Live;

        return db.Subscriptions.FirstOrDefaultAsync(
            subscription => subscription.SubjectKind == kind
                            && subscription.SubjectId == id
                            && live.Contains(subscription.Status),
            cancellationToken);
    }

    /// <summary>
    /// The plan and current version this name can actually be bought as, or the refusal saying why
    /// not.
    /// </summary>
    public async Task<(Plan Plan, PlanVersion Version)> RequirePurchasableAsync(
        string? planName, SubjectKind subjectKind, CancellationToken cancellationToken)
    {
        var name = planName?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CheckoutRefusedException(
                BillingErrorCodes.UnknownPlan, StatusCodes.Status400BadRequest,
                "No plan was named.");
        }

        var plan = await db.Plans.FirstOrDefaultAsync(
                       candidate => candidate.Name == name && candidate.ArchivedAt == null,
                       cancellationToken)
                   ?? throw new CheckoutRefusedException(
                       BillingErrorCodes.UnknownPlan, StatusCodes.Status400BadRequest,
                       $"'{name}' is not a plan this instance sells.");

        var version = await db.PlanVersions.FirstOrDefaultAsync(
                          candidate => candidate.PlanId == plan.Id
                                       && candidate.VersionNumber == plan.CurrentVersionNumber
                                       && candidate.ArchivedAt == null,
                          cancellationToken)
                      ?? throw NotPurchasable(name, "it has no current version to sell");

        if (version.PriceMinorUnits is not > 0 || string.IsNullOrWhiteSpace(version.Currency))
        {
            throw NotPurchasable(name, "it has no price - it is not something that is sold");
        }

        if (string.IsNullOrWhiteSpace(version.StripePriceId))
        {
            // The startup backfill exists to make this unreachable.
            throw NotPurchasable(name, "it has no price object in Stripe yet");
        }

        var kind = PlanSubjectKinds.Of(
            plan.Name,
            PlanCatalogueService.ReadValues(version.ValuesJson).Keys,
            options.DefaultUserPlan);

        if (kind != subjectKind)
        {
            throw NotPurchasable(name,
                $"it is a {EntitlementSubjectKinds.Of(kind)} plan and this is a "
                + $"{EntitlementSubjectKinds.Of(subjectKind)} subscription");
        }

        return (plan, version);
    }

    private static CheckoutRefusedException NotPurchasable(string plan, string because) =>
        new(BillingErrorCodes.NotPurchasable, StatusCodes.Status400BadRequest,
            $"'{plan}' cannot be bought: {because}.");

    /// <summary>Whether this caller may start or stop spending on this subject.</summary>
    private async Task RequireControlOfAsync(
        EntitlementSubject subject, string callerUserId, CancellationToken cancellationToken)
    {
        if (subject.Kind == SubjectKind.User)
        {
            if (!string.Equals(subject.Id, callerUserId, StringComparison.Ordinal))
            {
                throw CheckoutRefusedException.NotPermitted(
                    "A user plan can only be bought for the account buying it.");
            }

            return;
        }

        if (!await CanManageGuildAsync(subject.Id, callerUserId, cancellationToken))
        {
            throw CheckoutRefusedException.NotPermitted(
                "Managing the server is what it takes to put it on a paid plan.");
        }
    }

    /// <summary>Asks Guild, and fails closed.</summary>
    public async Task<bool> CanManageGuildAsync(
        string guildId, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(userId)) return false;

        try
        {
            var response = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(
                new HasUserPermissionToGuildRequest
                {
                    UserId = userId,
                    GuildId = guildId,
                    Permission = ExternalPermission.ManageGuild,
                }, cancellationToken);

            return response.IsAllowed;
        }
        catch (Exception exception)
        {
            logger?.LogError(exception,
                "Guild did not answer whether {User} can manage {Guild}, so the answer is no. This is "
                + "an outage rather than a permission decision.", userId, guildId);

            return false;
        }
    }

    private async Task<List<string>> ManageableGuildIdsAsync(
        string userId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await bus.InvokeAsync<ListManageableGuildsForUserResponse>(
                new ListManageableGuildsForUserRequest { UserId = userId }, cancellationToken);

            return response.Guilds.Select(guild => guild.Id).ToList();
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception,
                "Guild did not answer which guilds {User} manages, so their billing list shows only "
                + "what they pay for themselves.", userId);

            return [];
        }
    }

    private static void Mirror(Subscription row, StripeSubscriptionSnapshot snapshot) =>
        row.MirrorFromStripe(
            SubscriptionStatuses.Parse(snapshot.Status),
            snapshot.CurrentPeriodEnd,
            snapshot.CancelAtPeriodEnd,
            snapshot.LatestInvoiceId);

    private async Task<SubscriptionDto> ToDtoAsync(
        Subscription row, bool isPayer, CancellationToken cancellationToken)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(
            candidate => candidate.Id == row.PlanId, cancellationToken);

        // A subscription whose plan row is gone should be impossible: the foreign key is Restrict
        // and a plan somebody is on cannot be archived, let alone deleted.
        if (plan is null)
        {
            return new SubscriptionDto(
                row.StripeSubscriptionId,
                row.SubjectKind,
                row.SubjectId,
                row.PlanId,
                row.PlanId,
                row.VersionNumber,
                SubscriptionStatusWire.Of(row.Status),
                row.CurrentPeriodEnd,
                row.CancelAtPeriodEnd,
                row.GracePeriodEndsAt,
                null,
                null,
                null,
                isPayer);
        }

        var version = await db.PlanVersions.FirstOrDefaultAsync(
            candidate => candidate.PlanId == row.PlanId && candidate.VersionNumber == row.VersionNumber,
            cancellationToken);

        return SubscriptionDto.From(row, plan, version, isPayer);
    }
}
