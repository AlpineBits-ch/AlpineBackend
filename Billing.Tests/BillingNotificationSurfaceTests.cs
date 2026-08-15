using System.Reflection;
using Billing.Application.Endpoints;
using Billing.Application.Notifications;
using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Identity.Contracts.Bus.Commands;
using JasperFx.CodeGeneration.Frames;

namespace Billing.Tests;

/// <summary>The emission surface for billing email, pinned.</summary>
[TestFixture]
public class BillingNotificationSurfaceTests
{
    private static readonly Type[] NotificationTypes =
    [
        typeof(CreditIssuedNotification),
        typeof(EntitlementGrantNotification),
        typeof(PlanUpgradedNotification),
    ];

    private static readonly string[] NotificationNames =
        [.. NotificationTypes.Select(type => type.Name)];

    /// <summary>The four files in <c>Billing.Application</c> allowed to name a notification type at
    /// all: the factory, and the three endpoints that cascade what it builds.</summary>
    private static readonly string[] AllowedFiles =
    [
        "BillingNotices.cs",
        "CreditAdminEndpoint.cs",
        "GrantEndpoint.cs",
        "SubscriptionEndpoint.cs",
    ];

    // ── The source guard ─────────────────────────────────────────────────────

    private static string ApplicationRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "Billing.Application", "Endpoints")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate Billing.Application in the source tree");

        return Path.Combine(directory!.FullName, "Billing.Application");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(ApplicationRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Nothing outside the four allowed files so much as mentions a notification type.
    /// </summary>
    [Test]
    public void Only_the_factory_and_its_three_endpoints_name_a_notification_type()
    {
        var offenders = SourceFiles()
            .Where(path => NotificationNames.Any(name =>
                File.ReadAllText(path).Contains(name, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(offenders, Is.EqualTo(AllowedFiles.OrderBy(name => name, StringComparer.Ordinal).ToList()),
            "billing email is raised from endpoints, at human-initiated transitions, and nowhere else - "
            + "read BillingNotices' class comment before adding a file to this list");
    }

    /// <summary>The three producers that would each be their own incident, named individually so the
    /// failure message says which one somebody hooked.</summary>
    [TestCase("GrantExpirySweeper.cs", "it re-announces a 20 minute window every 5 minutes on purpose")]
    [TestCase("GrantStartSweeper.cs", "it re-announces a 20 minute window every 5 minutes on purpose")]
    [TestCase("PlanService.cs", "a version activation announces for every subject on the plan")]
    [TestCase("StripeWebhookProcessor.cs", "webhooks are replayed and delivered out of order by design")]
    [TestCase("SubscriptionReconciler.cs", "it reconciles against object state, repeatedly")]
    public void A_repeating_or_fan_out_producer_never_raises_billing_mail(string file, string why)
    {
        var path = SourceFiles().SingleOrDefault(candidate => Path.GetFileName(candidate) == file);

        Assert.That(path, Is.Not.Null, $"{file} has moved or been renamed; this guard is now testing nothing");

        var source = File.ReadAllText(path!);

        Assert.Multiple(() =>
        {
            foreach (var name in NotificationNames)
            {
                Assert.That(source, Does.Not.Contain(name),
                    $"{file} must not raise billing mail: {why}");
            }
        });
    }

    // ── The signature guard ──────────────────────────────────────────────────

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (!type.IsGenericType) yield break;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Unwrap(argument)) yield return nested;
        }
    }

    private static bool Yields(MethodInfo method) =>
        Unwrap(method.ReturnType).Any(NotificationTypes.Contains);

    /// <summary>The eight methods whose return type can carry a notification, pinned.</summary>
    [Test]
    public void Exactly_three_endpoints_and_one_factory_can_return_a_notification()
    {
        Type[] types;
        try
        {
            types = typeof(BillingNotices).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            // A Wolverine-generated type that will not load is not what is under test here.
            types = [.. partial.Types.Where(type => type is not null).Select(type => type!)];
        }

        var yielding = types
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(Yields)
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(yielding, Is.EqualTo(new[]
        {
            "BillingNotices.ForCreditIssue",
            "BillingNotices.ForGrant",
            "BillingNotices.ForPlanChange",
            "CreditAdminEndpoint.IssueAsync",
            "GrantEndpoint.AmendExpiryAsync",
            "GrantEndpoint.IssueAsync",
            "GrantEndpoint.RevokeAsync",
            "SubscriptionEndpoint.ChangeAsync",
        }));
    }

    /// <summary>Wolverine really does see the notification as a cascaded message.</summary>
    [TestCase(typeof(GrantEndpoint), nameof(GrantEndpoint.IssueAsync), typeof(EntitlementGrantNotification))]
    [TestCase(typeof(GrantEndpoint), nameof(GrantEndpoint.RevokeAsync), typeof(EntitlementGrantNotification))]
    [TestCase(typeof(GrantEndpoint), nameof(GrantEndpoint.AmendExpiryAsync), typeof(EntitlementGrantNotification))]
    [TestCase(typeof(CreditAdminEndpoint), nameof(CreditAdminEndpoint.IssueAsync), typeof(CreditIssuedNotification))]
    [TestCase(typeof(SubscriptionEndpoint), nameof(SubscriptionEndpoint.ChangeAsync), typeof(PlanUpgradedNotification))]
    public void The_framework_unpacks_the_notification_from_the_endpoints_return(
        Type endpoint, string method, Type notification)
    {
        var call = new MethodCall(endpoint, method);

        Assert.That(call.Creates.Select(variable => variable.VariableType), Does.Contain(notification),
            $"{endpoint.Name}.{method} returns the notification but Wolverine does not see it as a "
            + "cascaded message, so nothing would ever be sent");
    }

    /// <summary>The sweeps still produce invalidations and only invalidations.</summary>
    [Test]
    public void The_two_sweepers_still_collect_nothing_but_invalidations()
    {
        var collectors = new[]
        {
            typeof(GrantExpirySweeper).GetMethod(
                "CollectAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
            typeof(GrantStartSweeper).GetMethod(
                "CollectAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
        };

        Assert.Multiple(() =>
        {
            foreach (var collector in collectors)
            {
                Assert.That(collector, Is.Not.Null, "a sweeper's CollectAsync has been renamed");
                Assert.That(Unwrap(collector!.ReturnType), Does.Contain(typeof(EntitlementsChanged)));
                Assert.That(Yields(collector), Is.False,
                    "a sweeper announces the same expiry three or four times by design");
            }
        });
    }
}
