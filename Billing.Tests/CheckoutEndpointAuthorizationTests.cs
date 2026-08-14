using System.Reflection;
using Billing.Application.Endpoints;
using Billing.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace Billing.Tests;

/// <summary>Who may reach the customer-facing billing surface.</summary>
[TestFixture]
public class CheckoutEndpointAuthorizationTests
{
    private static readonly Type[] CheckoutEndpoints =
    [
        typeof(CatalogueEndpoint),
        typeof(SubscriptionEndpoint),
        typeof(PaymentMethodEndpoint),
    ];

    private static IReadOnlyList<MethodInfo> Endpoints(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttributes()
                .Any(attribute => attribute.GetType().Name.StartsWith("Wolverine", StringComparison.Ordinal)
                                  && attribute.GetType().Name.EndsWith("Attribute", StringComparison.Ordinal)))
            .ToList();

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void Every_checkout_endpoint_requires_an_authenticated_caller()
    {
        var found = 0;

        Assert.Multiple(() =>
        {
            foreach (var type in CheckoutEndpoints)
            {
                foreach (var method in Endpoints(type))
                {
                    found++;

                    Assert.That(method.GetCustomAttribute<AuthorizeAttribute>(), Is.Not.Null,
                        $"{type.Name}.{method.Name} is reachable anonymously");
                }
            }
        });

        Assert.That(found, Is.GreaterThan(0), "reflection found no endpoints, so this proves nothing");
    }

    /// <summary>No staff policy on any of these.</summary>
    [Test]
    public void No_checkout_endpoint_is_gated_on_a_staff_policy()
    {
        Assert.Multiple(() =>
        {
            foreach (var type in CheckoutEndpoints)
            {
                foreach (var method in Endpoints(type))
                {
                    var policy = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

                    Assert.That(policy, Is.Null.Or.Empty,
                        $"{type.Name}.{method.Name} is behind '{policy}'. These are a customer's own "
                        + "records, not a staff surface.");

                    Assert.That(policy, Is.Not.EqualTo(BillingPolicies.GrantAdmin));
                    Assert.That(policy, Is.Not.EqualTo(BillingPolicies.GrantRead));
                }
            }
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>Every route takes the caller from the token.</summary>
    [Test]
    public void No_payment_method_route_takes_a_user_or_guild_id()
    {
        Assert.Multiple(() =>
        {
            foreach (var method in Endpoints(typeof(PaymentMethodEndpoint)))
            {
                var names = method.GetParameters().Select(parameter => parameter.Name!.ToLowerInvariant());

                Assert.That(names, Has.None.Contains("userid").And.None.Contains("guildid")
                        .And.None.Contains("subject"),
                    $"{method.Name} accepts somebody else's identity");
            }
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>Cancellation is at period end and refunds are a staff action in the support console.
    /// A DELETE on a subscription would be the shape of an immediate cancellation, which this surface
    /// deliberately does not have.</summary>
    [Test]
    public void There_is_no_delete_on_the_subscription_surface()
    {
        var deletes = Endpoints(typeof(SubscriptionEndpoint))
            .Where(method => method.GetCustomAttributes()
                .Any(attribute => attribute.GetType().Name == "WolverineDeleteAttribute"))
            .Select(method => method.Name);

        Assert.That(deletes, Is.Empty);
    }
}
