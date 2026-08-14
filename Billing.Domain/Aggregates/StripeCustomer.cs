using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>The link between one account here and one customer object in Stripe.</summary>
public class StripeCustomer : BaseEntity<StripeCustomer>, IPrefixedEntity
{
    public static string Prefix { get; } = "stcu";

    /// <summary>An opaque user id.</summary>
    public string UserId { get; set; } = null!;

    /// <summary><c>cus_...</c>, as Stripe issued it.</summary>
    public string StripeCustomerId { get; set; } = null!;
}
