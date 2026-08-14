namespace Billing.Application.Stripe;

/// <summary>A deterministic key for one Stripe write, in a type rather than a string.</summary>
public readonly record struct StripeIdempotencyKey
{
    /// <summary>Everything this instance writes is namespaced, so a key can never collide with one
    /// issued by anything else sharing the account.</summary>
    public const string Namespace = "venta";

    public string Value { get; }

    private StripeIdempotencyKey(string value) => Value = value;

    /// <summary><c>venta:product:{planId}</c>.</summary>
    public static StripeIdempotencyKey ForProduct(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        return new StripeIdempotencyKey($"{Namespace}:product:{planId}");
    }

    /// <summary><c>venta:price:{planId}:v{versionNumber}:{currency}:{interval}</c>.</summary>
    public static StripeIdempotencyKey ForPrice(
        string planId, int versionNumber, string currency, string interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);

        return new StripeIdempotencyKey(
            $"{Namespace}:price:{planId}:v{versionNumber}:{currency.ToLowerInvariant()}:{interval}");
    }

    /// <summary><c>venta:customer:{userId}</c>.</summary>
    public static StripeIdempotencyKey ForCustomer(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return new StripeIdempotencyKey($"{Namespace}:customer:{userId}");
    }

    /// <summary>
    /// <c>venta:subscription:{subjectKind}:{subjectId}:{priceId}</c>, with <c>:untaxed</c> appended
    /// for the no-automatic-tax fallback.
    /// </summary>
    public static StripeIdempotencyKey ForSubscription(
        string subjectKind, string subjectId, string priceId, bool automaticTax = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceId);

        var suffix = automaticTax ? string.Empty : ":untaxed";

        return new StripeIdempotencyKey(
            $"{Namespace}:subscription:{subjectKind.ToLowerInvariant()}:{subjectId}:{priceId}{suffix}");
    }

    /// <summary>
    /// <c>venta:{operation}:{objectId}:{unique}</c> - the one factory whose key is fresh per call.
    /// </summary>
    public static StripeIdempotencyKey ForRequest(string operation, string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return new StripeIdempotencyKey(
            $"{Namespace}:{operation}:{objectId}:{Guid.NewGuid():N}");
    }

    public override string ToString() => Value;
}

/// <summary>What to create a Stripe <c>Product</c> as.</summary>
public sealed record StripeProductRequest(
    string Name,
    string? Description,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>What to create a Stripe <c>Price</c> as.</summary>
public sealed record StripePriceRequest(
    string ProductId,
    long UnitAmountMinorUnits,
    string Currency,
    string Interval,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A Stripe object id, on its own.</summary>
public sealed record StripeObjectRef(string Id);

/// <summary>What we read off a live Stripe subscription.</summary>
public sealed record StripeSubscriptionSnapshot(
    string Id,
    string Status,
    string? CustomerId,
    string? PriceId,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    string? LatestInvoiceId,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A billing address, which <c>automatic_tax</c> cannot be enabled without.</summary>
public sealed record StripeAddress(
    string Country,
    string? Line1 = null,
    string? Line2 = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null);

/// <summary>What to create a Stripe <c>Customer</c> as.</summary>
public sealed record StripeCustomerRequest(
    string? Email,
    StripeAddress? Address,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>What to create a Stripe <c>Subscription</c> as.</summary>
public sealed record StripeSubscriptionRequest(
    string CustomerId,
    string PriceId,
    bool AutomaticTax,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A newly created subscription and the secret the client confirms against.</summary>
public sealed record StripeSubscriptionResult(StripeSubscriptionSnapshot Subscription, string? ClientSecret);

/// <summary>Everything we are ever allowed to know about a card.</summary>
public sealed record StripePaymentMethodSummary(
    string Id,
    string? Brand,
    string? Last4,
    long? ExpMonth,
    long? ExpYear,
    bool IsDefault);

/// <summary>One invoice, as a list screen reads it.</summary>
public sealed record StripeInvoiceSummary(
    string Id,
    string? Number,
    string? Status,
    long AmountDueMinorUnits,
    string Currency,
    DateTimeOffset CreatedAt,
    string? HostedInvoiceUrl,
    string? InvoicePdfUrl);

/// <summary>One line of a proration preview.</summary>
public sealed record StripeProrationLine(string Description, long AmountMinorUnits);

/// <summary>What changing plan would cost, before it is committed.</summary>
public sealed record StripeProrationPreview(
    long ImmediateChargeMinorUnits,
    string Currency,
    long NextInvoiceTotalMinorUnits,
    DateTimeOffset? NextInvoiceAt,
    IReadOnlyList<StripeProrationLine> Lines);

/// <summary>Stripe refused, or could not be reached.</summary>
public sealed class StripeGatewayException(
    string operation, string message, Exception? inner = null, string? stripeCode = null)
    : Exception(message, inner)
{
    public string Operation { get; } = operation;

    /// <summary>Stripe's own error code, when it sent one.</summary>
    public string? StripeCode { get; } = stripeCode;

    /// <summary>
    /// Whether Stripe refused because it could not work out the tax, rather than because anything
    /// about the purchase was wrong.
    /// </summary>
    public bool IsTaxFailure =>
        StripeCode is not null && StripeCode.Contains("tax", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The whole of this service's contact with Stripe.</summary>
public interface IStripeGateway
{
    /// <summary>Creates the Product, or returns the one the same key already created.</summary>
    Task<StripeObjectRef> CreateProductAsync(
        StripeProductRequest request, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Creates the Price, or returns the one the same key already created.</summary>
    Task<StripeObjectRef> CreatePriceAsync(
        StripePriceRequest request, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Reads a subscription as Stripe currently sees it.</summary>
    Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(
        string subscriptionId, CancellationToken cancellationToken);

    /// <summary>Creates the Customer, or returns the one the same key already created.</summary>
    Task<StripeObjectRef> CreateCustomerAsync(
        StripeCustomerRequest request, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Puts an address on an existing customer, which is what makes automatic tax possible
    /// for somebody who bought before we asked for one.</summary>
    Task UpdateCustomerAddressAsync(
        string customerId,
        StripeAddress address,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates the subscription with <c>payment_behavior: default_incomplete</c> and returns the
    /// secret the Payment Element confirms against.
    /// </summary>
    Task<StripeSubscriptionResult> CreateSubscriptionAsync(
        StripeSubscriptionRequest request,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// The subscription as Stripe sees it now, together with a freshly retrieved client secret.
    /// </summary>
    Task<StripeSubscriptionResult?> GetSubscriptionWithSecretAsync(
        string subscriptionId, CancellationToken cancellationToken);

    /// <summary>Cancels an <c>incomplete</c> subscription outright, now.</summary>
    Task CancelIncompleteSubscriptionAsync(
        string subscriptionId, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Sets or clears <c>cancel_at_period_end</c>.</summary>
    Task<StripeSubscriptionSnapshot> SetCancelAtPeriodEndAsync(
        string subscriptionId,
        bool cancelAtPeriodEnd,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Moves the subscription's single item onto another price, prorating.</summary>
    Task<StripeSubscriptionSnapshot> ChangeSubscriptionPriceAsync(
        string subscriptionId,
        string priceId,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>What that change would cost, from Stripe's own upcoming-invoice preview.</summary>
    Task<StripeProrationPreview> PreviewSubscriptionPriceChangeAsync(
        string subscriptionId, string priceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StripePaymentMethodSummary>> ListPaymentMethodsAsync(
        string customerId, CancellationToken cancellationToken);

    /// <summary>Creates a SetupIntent for adding a card outside a purchase, and returns its client
    /// secret.</summary>
    Task<string?> CreateSetupIntentAsync(
        string customerId, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    Task SetDefaultPaymentMethodAsync(
        string customerId,
        string paymentMethodId,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    Task DetachPaymentMethodAsync(
        string paymentMethodId, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<StripeInvoiceSummary>> ListInvoicesAsync(
        string customerId, int limit, CancellationToken cancellationToken);
}
