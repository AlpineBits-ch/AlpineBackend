using System.Reflection;
using Billing.Application.Endpoints;
using Billing.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace Billing.Tests;

/// <summary>Who may read the promotion surface and who may change it.</summary>
[TestFixture]
public class PromotionEndpointAuthorizationTests
{
    private static IReadOnlyList<(MethodInfo Method, bool Writes)> Endpoints() =>
        typeof(PromotionAdminEndpoint).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => (Method: method, Verbs: Verbs(method)))
            .Where(endpoint => endpoint.Verbs.Count > 0)
            .Select(endpoint => (endpoint.Method,
                Writes: endpoint.Verbs.Any(verb => verb != "WolverineGetAttribute")))
            .ToList();

    private static List<string> Verbs(MethodInfo method) =>
        method.GetCustomAttributes()
            .Select(attribute => attribute.GetType().Name)
            .Where(name => name.StartsWith("Wolverine", StringComparison.Ordinal)
                           && name.EndsWith("Attribute", StringComparison.Ordinal))
            .ToList();

    [Test]
    public void Every_endpoint_declares_a_policy()
    {
        var endpoints = Endpoints();

        Assert.That(endpoints, Is.Not.Empty, "reflection found no endpoints, so this proves nothing");

        Assert.Multiple(() =>
        {
            foreach (var (method, _) in endpoints)
            {
                var policy = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

                Assert.That(policy, Is.Not.Null.And.Not.Empty,
                    $"{method.Name} is reachable with no policy at all");
                Assert.That(policy, Is.AnyOf(BillingPolicies.GrantAdmin, BillingPolicies.GrantRead));
            }
        });
    }

    [Test]
    public void Reads_admit_moderators_and_everything_that_writes_is_admin_only()
    {
        Assert.Multiple(() =>
        {
            foreach (var (method, writes) in Endpoints())
            {
                var policy = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

                Assert.That(policy,
                    Is.EqualTo(writes ? BillingPolicies.GrantAdmin : BillingPolicies.GrantRead),
                    writes
                        ? $"{method.Name} hands out a paid plan and must be Admin only"
                        : $"{method.Name} is a read and a Moderator answering a ticket needs it");
            }
        });
    }

    /// <summary><b>The one that matters most on this surface.</b> A deleted redemption is a re-granted
    /// trial and a deleted campaign takes the explanation for every redemption it produced with it, so
    /// there is deliberately nothing here that removes either. Adding a delete would be the single
    /// change that undoes the wave.</summary>
    [Test]
    public void There_is_no_delete_on_the_promotion_surface()
    {
        var deletes = typeof(PromotionAdminEndpoint)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => Verbs(method).Contains("WolverineDeleteAttribute"))
            .Select(method => method.Name);

        Assert.That(deletes, Is.Empty);
    }
}
