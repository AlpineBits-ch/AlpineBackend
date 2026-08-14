using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>
/// The link between an account here and its customer object in Stripe, created on demand.
/// </summary>
public sealed class StripeCustomerRegistry(
    MicroserviceContext db,
    IStripeGateway gateway,
    ILogger<StripeCustomerRegistry>? logger = null)
{
    /// <summary>Stripe metadata carrying our account id, so a person staring at the dashboard - or a
    /// webhook - can get from a customer back to an account. The same const the reconciler reads it
    /// back by, rather than a second literal that agrees with it today.</summary>
    public const string PayerMetadataKey = SubscriptionReconciler.PayerUserIdMetadataKey;

    /// <summary>The row for this account, or null when they have never paid for anything.</summary>
    public Task<StripeCustomer?> FindAsync(string userId, CancellationToken cancellationToken) =>
        db.StripeCustomers.FirstOrDefaultAsync(
            customer => customer.UserId == userId, cancellationToken);

    /// <summary>
    /// The row for this account, creating the Stripe customer if there is not one yet.
    /// </summary>
    public async Task<StripeCustomer> EnsureAsync(
        string userId,
        string? email,
        StripeAddress? address,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var existing = await FindAsync(userId, cancellationToken);

        if (existing is not null)
        {
            if (address is not null)
            {
                await gateway.UpdateCustomerAddressAsync(
                    existing.StripeCustomerId,
                    address,
                    StripeIdempotencyKey.ForRequest("customer_address", existing.StripeCustomerId),
                    cancellationToken);
            }

            return existing;
        }

        var created = await gateway.CreateCustomerAsync(
            new StripeCustomerRequest(
                email,
                address,
                new Dictionary<string, string> { [PayerMetadataKey] = userId }),
            StripeIdempotencyKey.ForCustomer(userId),
            cancellationToken);

        var row = new StripeCustomer
        {
            Id = StripeCustomer.GenerateId(),
            UserId = userId,
            StripeCustomerId = created.Id,
        };

        // Added, not saved: the caller is inside a Wolverine handler whose middleware commits, and
        // this row has to land in the same transaction as the subscription that needed it.
        db.StripeCustomers.Add(row);

        logger?.LogInformation(
            "Account {User} is now Stripe customer {Customer}.", userId, created.Id);

        return row;
    }
}
