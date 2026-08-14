using System.Reflection;
using Billing.Application.Endpoints;
using Billing.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace Billing.Tests;

/// <summary>Who may read the plan catalogue and who may change it.</summary>
[TestFixture]
public class PlanEndpointAuthorizationTests
{
    /// <summary>Matched by attribute name rather than by a shared base type, which Wolverine does
    /// not expose as a stable public one. The names are the contract either way: an endpoint is
    /// whatever carries a <c>Wolverine*</c> verb attribute.</summary>
    private static IReadOnlyList<(MethodInfo Method, bool Writes)> Endpoints() =>
        typeof(PlanEndpoint).GetMethods(BindingFlags.Public | BindingFlags.Static)
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
                        ? $"{method.Name} changes what a plan gives and must be Admin only"
                        : $"{method.Name} is a read and a Moderator answering a ticket needs it");
            }
        });
    }

    /// <summary>A plan is never hard-deleted, so there is nothing here to delete it with.</summary>
    [Test]
    public void There_is_no_delete_on_the_plan_surface()
    {
        var deletes = typeof(PlanEndpoint)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => Verbs(method).Contains("WolverineDeleteAttribute"))
            .Select(method => method.Name);

        Assert.That(deletes, Is.Empty);
    }
}
