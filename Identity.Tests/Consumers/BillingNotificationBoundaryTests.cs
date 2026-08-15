using System.Reflection;
using Identity.Application.Consumers;

namespace Identity.Tests.Consumers;

/// <summary>
/// The boundary that makes the sweeper and mass-mail failure modes impossible rather than merely
/// forbidden.
/// </summary>
[TestFixture]
[Category("Unit")]
public class BillingNotificationBoundaryTests
{
    private static Assembly IdentityApplication => typeof(BillingNotificationHandler).Assembly;

    [Test]
    public void Identity_does_not_reference_any_Billing_assembly()
    {
        var billing = IdentityApplication.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Billing.", StringComparison.Ordinal))
            .ToList();

        Assert.That(billing, Is.Empty,
            "Billing raises a notification intent into Identity.Contracts and Identity renders it. A "
            + "reference the other way makes EntitlementsChanged - an invalidation that is re-announced "
            + "on purpose and fanned out to a whole plan - available to hook, which is the one thing "
            + "this design exists to prevent.");
    }

    /// <summary>Belt to the reference check's braces: it also catches a locally redeclared copy of the
    /// event, which would restore the hazard without restoring the reference.</summary>
    [Test]
    public void Nothing_in_Identity_handles_an_entitlements_changed_event()
    {
        Type[] types;
        try
        {
            types = IdentityApplication.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            types = [.. partial.Types.Where(type => type is not null).Select(type => type!)];
        }

        var handlers = types
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(method => method.Name is "Handle" or "Handles" or "Consume")
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType.Name.Contains("EntitlementsChanged", StringComparison.Ordinal)))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToList();

        Assert.That(handlers, Is.Empty,
            "an entitlement invalidation is not a notification - see this fixture's remarks");
    }
}
