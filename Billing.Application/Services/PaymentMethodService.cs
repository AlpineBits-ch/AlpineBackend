using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>Cards and invoices, for the account that owns them.</summary>
public sealed class PaymentMethodService(
    MicroserviceContext db,
    IStripeGateway gateway,
    StripeCustomerRegistry customers,
    ILogger<PaymentMethodService>? logger = null)
{
    /// <summary>How many invoices one page returns.</summary>
    public const int InvoicePageSize = 24;

    public async Task<IReadOnlyList<PaymentMethodDto>> ListAsync(
        string callerUserId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var customer = await customers.FindAsync(callerUserId, cancellationToken);

        // Never paid for anything, so there is nothing attached and nothing to create a customer for.
        // An empty list rather than a 404: "you have no cards" is the answer, not a failure.
        if (customer is null) return [];

        var methods = await gateway.ListPaymentMethodsAsync(
            customer.StripeCustomerId, cancellationToken);

        return methods
            .Select(method => new PaymentMethodDto(
                method.Id, method.Brand, method.Last4, method.ExpMonth, method.ExpYear, method.IsDefault))
            .ToList();
    }

    /// <summary>The secret for adding a card outside a purchase.</summary>
    public async Task<SetupIntentDto> CreateSetupIntentAsync(
        string callerUserId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var customer = await customers.EnsureAsync(
            callerUserId, email: null, address: null, cancellationToken);

        var secret = await gateway.CreateSetupIntentAsync(
            customer.StripeCustomerId,
            StripeIdempotencyKey.ForRequest("setup_intent", customer.StripeCustomerId),
            cancellationToken);

        return new SetupIntentDto(secret);
    }

    public async Task SetDefaultAsync(
        string callerUserId, string paymentMethodId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var (customer, _) = await RequireOwnedAsync(callerUserId, paymentMethodId, cancellationToken);

        await gateway.SetDefaultPaymentMethodAsync(
            customer.StripeCustomerId,
            paymentMethodId,
            StripeIdempotencyKey.ForRequest("default_payment_method", customer.StripeCustomerId),
            cancellationToken);
    }

    /// <summary>
    /// Detaches a card, unless it is the last one holding up a live subscription.
    /// </summary>
    public async Task DetachAsync(
        string callerUserId, string paymentMethodId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var (customer, methods) = await RequireOwnedAsync(callerUserId, paymentMethodId, cancellationToken);

        if (methods.Count == 1 && await HasLiveSubscriptionAsync(callerUserId, cancellationToken))
        {
            throw new CheckoutRefusedException(
                BillingErrorCodes.LastPaymentMethod, StatusCodes.Status409Conflict,
                "This is the only card on the account and a live subscription is being charged to it. "
                + "Add another card first, or cancel the subscription - removing it now would mean the "
                + "next invoice simply fails.");
        }

        await gateway.DetachPaymentMethodAsync(
            paymentMethodId,
            StripeIdempotencyKey.ForRequest("detach", paymentMethodId),
            cancellationToken);

        logger?.LogInformation(
            "Account {User} detached a payment method from Stripe customer {Customer}.",
            callerUserId, customer.StripeCustomerId);
    }

    public async Task<IReadOnlyList<InvoiceDto>> ListInvoicesAsync(
        string callerUserId, CancellationToken cancellationToken)
    {
        RequireStripe();

        var customer = await customers.FindAsync(callerUserId, cancellationToken);
        if (customer is null) return [];

        var invoices = await gateway.ListInvoicesAsync(
            customer.StripeCustomerId, InvoicePageSize, cancellationToken);

        return invoices
            .Select(invoice => new InvoiceDto(
                invoice.Id,
                invoice.Number,
                invoice.Status,
                invoice.AmountDueMinorUnits,
                invoice.Currency,
                invoice.CreatedAt,
                invoice.HostedInvoiceUrl,
                invoice.InvoicePdfUrl))
            .ToList();
    }

    /// <summary>
    /// The caller's Stripe customer and their cards, having established that the card they named is
    /// one of them.
    /// </summary>
    private async Task<(StripeCustomer Customer, IReadOnlyList<StripePaymentMethodSummary> Methods)>
        RequireOwnedAsync(string callerUserId, string paymentMethodId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(paymentMethodId)) throw UnknownPaymentMethod();

        var customer = await customers.FindAsync(callerUserId, cancellationToken)
                       ?? throw UnknownPaymentMethod();

        var methods = await gateway.ListPaymentMethodsAsync(
            customer.StripeCustomerId, cancellationToken);

        if (methods.All(method => !string.Equals(method.Id, paymentMethodId, StringComparison.Ordinal)))
        {
            throw UnknownPaymentMethod();
        }

        return (customer, methods);
    }

    private Task<bool> HasLiveSubscriptionAsync(string callerUserId, CancellationToken cancellationToken)
    {
        var live = SubscriptionStatuses.Live;

        return db.Subscriptions.AnyAsync(
            subscription => subscription.PayerUserId == callerUserId
                            && live.Contains(subscription.Status),
            cancellationToken);
    }

    private static CheckoutRefusedException UnknownPaymentMethod() =>
        new(BillingErrorCodes.UnknownPaymentMethod, StatusCodes.Status404NotFound,
            "No payment method with that id on this account.");

    private static void RequireStripe()
    {
        if (!Env.License.IsStripeConfigured) throw CheckoutRefusedException.BillingDisabled();
    }
}
