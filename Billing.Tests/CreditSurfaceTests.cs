using System.Reflection;
using Billing.Application.Credit;
using Billing.Application.Endpoints;
using Billing.Application.Security;
using Billing.Domain.Aggregates;
using Microsoft.AspNetCore.Authorization;

namespace Billing.Tests;

/// <summary>The credit surface, and the hard constraints it is not allowed to grow past.</summary>
[TestFixture]
public class CreditSurfaceTests
{
    private static List<string> Verbs(MethodInfo method) =>
        method.GetCustomAttributes()
            .Select(attribute => attribute.GetType().Name)
            .Where(name => name.StartsWith("Wolverine", StringComparison.Ordinal)
                           && name.EndsWith("Attribute", StringComparison.Ordinal))
            .ToList();

    private static IReadOnlyList<MethodInfo> EndpointsOf(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => Verbs(method).Count > 0)
            .ToList();

    private static IReadOnlyList<string> RoutesOf(Type type) =>
        EndpointsOf(type)
            .SelectMany(method => method.GetCustomAttributes()
                .Where(attribute => attribute.GetType().Name.StartsWith("Wolverine", StringComparison.Ordinal))
                .Select(attribute => (string?)attribute.GetType()
                    .GetProperty("Template")?.GetValue(attribute))
                .Where(template => template is not null)
                .Select(template => template!))
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToList();

    // ── the hard constraints ─────────────────────────────────────────────────

    /// <summary>The whole user-facing surface, pinned.</summary>
    [Test]
    public void The_user_facing_credit_surface_is_exactly_four_read_and_spend_routes()
    {
        Assert.That(RoutesOf(typeof(CreditEndpoint)), Is.EqualTo(new[]
        {
            "/api/v1/credit/me",
            "/api/v1/credit/me/catalogue",
            "/api/v1/credit/me/ledger",
            "/api/v1/credit/me/purchases",
        }));
    }

    /// <summary>
    /// The staff surface, pinned for the same reason and with one extra: there is no route here
    /// that sells credit either.
    /// </summary>
    [Test]
    public void The_admin_credit_surface_issues_corrects_and_reverses_and_never_sells()
    {
        Assert.That(RoutesOf(typeof(CreditAdminEndpoint)), Is.EqualTo(new[]
        {
            "/api/v1/credit/campaigns",
            "/api/v1/credit/campaigns",
            "/api/v1/credit/campaigns/{codeOrId}/budget",
            "/api/v1/credit/campaigns/{codeOrId}/pause",
            "/api/v1/credit/entries/{entryId}/reverse",
            "/api/v1/credit/wallets/{userId}",
            "/api/v1/credit/wallets/{userId}/adjust",
            "/api/v1/credit/wallets/{userId}/issue",
            "/api/v1/credit/wallets/{userId}/ledger",
            "/api/v1/credit/wallets/{userId}/rebuild",
            "/api/v1/credit/wallets/{userId}/void",
        }));
    }

    /// <summary>Every route on both classes read together, checked against the vocabulary that would
    /// mean somebody had built a way in or out. Belt to the pinned lists' braces, because a rename
    /// would slip past a list and not past this.</summary>
    [Test]
    public void No_credit_route_buys_sells_gifts_refunds_or_withdraws()
    {
        var forbidden = new[]
        {
            "topup", "top-up", "buy", "purchase-credit", "credits/buy", "gift", "transfer", "send",
            "refund", "withdraw", "cash", "redeem-for", "convert", "checkout",
        };

        var routes = RoutesOf(typeof(CreditEndpoint)).Concat(RoutesOf(typeof(CreditAdminEndpoint))).ToList();

        Assert.Multiple(() =>
        {
            foreach (var route in routes)
            {
                foreach (var word in forbidden)
                {
                    Assert.That(route.Contains(word, StringComparison.OrdinalIgnoreCase), Is.False,
                        $"'{route}' looks like a way to turn money into credit or credit into money. "
                        + "monetization.md section 8.1 rules that out as a hard constraint, not a "
                        + "phase-one simplification.");
                }
            }
        });
    }

    /// <summary>A SKU can only ever be a plan for a number of days.</summary>
    [Test]
    public void A_credit_sku_has_exactly_one_possible_shape()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enum.GetValues<CreditSkuKind>(), Is.EqualTo(new[]
            {
                CreditSkuKind.TimeBoxedPlanGrant,
            }));

            // No property on the SKU names an entitlement key, so there is nowhere to put "one more
            // emoji slot" even if somebody wanted to.
            Assert.That(
                typeof(CreditSkuOptions).GetProperties().Select(property => property.Name),
                Does.Not.Contain("Entitlements").And.Not.Contain("EntitlementKey"));
        });
    }

    /// <summary>Section 8.1: credit has no cash value, expires, and says so wherever it is displayed.
    /// Every user-facing payload carries the sentence, so no client can decide to leave it off.
    /// </summary>
    [Test]
    public void Every_user_facing_payload_carries_the_no_cash_value_notice()
    {
        Assert.Multiple(() =>
        {
            foreach (var type in new[]
                     {
                         typeof(CreditWalletDto), typeof(CreditLedgerDto), typeof(CreditCatalogueDto),
                     })
            {
                Assert.That(type.GetProperty("Disclaimer"), Is.Not.Null, $"{type.Name} shows a balance");
                Assert.That(type.GetProperty("DisclaimerKey"), Is.Not.Null);
            }

            Assert.That(CreditDisclaimer.Text, Does.Contain("no cash value"));
            Assert.That(CreditDisclaimer.Text, Does.Contain("expires"));
            Assert.That(CreditDisclaimer.Text, Does.Contain("transferred"));
        });
    }

    /// <summary>Points, not currency.</summary>
    [Test]
    public void Nothing_in_the_wallet_carries_a_currency()
    {
        Assert.Multiple(() =>
        {
            foreach (var type in new[]
                     {
                         typeof(CreditEntry), typeof(CreditLot), typeof(CreditWallet),
                         typeof(CreditCampaign), typeof(CreditWalletDto), typeof(CreditLotDto),
                     })
            {
                Assert.That(
                    type.GetProperties().Select(property => property.Name),
                    Does.Not.Contain("Currency").And.Not.Contain("CurrencyCode"),
                    $"{type.Name} is denominated in points");
            }

            // The cash price is the exception and belongs on the SKU, because section 8.1 requires
            // one to exist and section 8.2 requires it to be shown beside the point price.
            Assert.That(typeof(CreditSkuDto).GetProperty("CashCurrency"), Is.Not.Null);
        });
    }

    /// <summary>The peg is a tool for setting prices, never a published exchange rate.</summary>
    [Test]
    public void The_internal_peg_is_not_on_any_wire_type()
    {
        Assert.Multiple(() =>
        {
            foreach (var type in new[]
                     {
                         typeof(CreditWalletDto), typeof(CreditCatalogueDto), typeof(CreditSkuDto),
                         typeof(CreditLedgerDto), typeof(CreditPurchaseDto),
                     })
            {
                Assert.That(
                    type.GetProperties().Select(property => property.Name),
                    Does.Not.Contain("PointsPerEuro").And.Not.Contain("ExchangeRate")
                        .And.Not.Contain("Peg"));
            }
        });
    }

    // ── access ───────────────────────────────────────────────────────────────

    /// <summary>Same split as the grant surface.</summary>
    [Test]
    public void Every_admin_credit_endpoint_declares_a_policy_and_writes_are_admin_only()
    {
        var endpoints = EndpointsOf(typeof(CreditAdminEndpoint));

        Assert.That(endpoints, Is.Not.Empty, "reflection found no endpoints, so this proves nothing");

        Assert.Multiple(() =>
        {
            foreach (var method in endpoints)
            {
                var policy = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy;
                var writes = Verbs(method).Any(verb => verb != "WolverineGetAttribute");

                Assert.That(policy, Is.Not.Null.And.Not.Empty,
                    $"{method.Name} is reachable with no policy at all");

                Assert.That(policy, Is.EqualTo(writes ? BillingPolicies.GrantAdmin : BillingPolicies.GrantRead),
                    $"{method.Name} is a {(writes ? "write" : "read")}");
            }
        });
    }

    /// <summary>
    /// Every user-facing route is authenticated and reads the caller from the token.
    /// </summary>
    [Test]
    public void Every_user_facing_credit_endpoint_is_authenticated_and_scoped_to_the_caller()
    {
        var endpoints = EndpointsOf(typeof(CreditEndpoint));

        Assert.That(endpoints, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var method in endpoints)
            {
                Assert.That(method.GetCustomAttribute<AuthorizeAttribute>(), Is.Not.Null,
                    $"{method.Name} is anonymous");
                Assert.That(method.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
            }

            foreach (var route in RoutesOf(typeof(CreditEndpoint)))
            {
                Assert.That(route, Does.Not.Contain("{userId}"));
            }
        });
    }

    // ── the ledger's own shape ───────────────────────────────────────────────

    /// <summary>
    /// There is no mutable balance column on anything that is not explicitly a cache.
    /// </summary>
    [Test]
    public void The_only_stored_balance_is_the_rebuildable_cache()
    {
        Assert.Multiple(() =>
        {
            foreach (var type in new[] { typeof(CreditEntry), typeof(CreditLot) })
            {
                Assert.That(
                    type.GetProperties().Select(property => property.Name),
                    Does.Not.Contain("Balance").And.Not.Contain("Remaining"),
                    $"{type.Name} must not carry a running total");
            }

            Assert.That(typeof(CreditWallet).GetProperty("CachedBalance"), Is.Not.Null);
            Assert.That(typeof(CreditLedgerService).GetMethod("RebuildAsync"), Is.Not.Null,
                "a cache is only allowed to exist because it can be rebuilt");
        });
    }

    /// <summary>The five kinds from section 8.5, and no sixth.</summary>
    [Test]
    public void The_ledger_has_exactly_the_five_specified_kinds()
    {
        Assert.That(Enum.GetValues<CreditEntryKind>(), Is.EqualTo(new[]
        {
            CreditEntryKind.Issue,
            CreditEntryKind.Spend,
            CreditEntryKind.Expiry,
            CreditEntryKind.Reversal,
            CreditEntryKind.Adjustment,
        }));
    }

    [Test]
    public void Only_adjustments_and_reversals_require_a_reason()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CreditEntry.RequiresReason(CreditEntryKind.Adjustment), Is.True);
            Assert.That(CreditEntry.RequiresReason(CreditEntryKind.Reversal), Is.True);
            Assert.That(CreditEntry.RequiresReason(CreditEntryKind.Issue), Is.False);
            Assert.That(CreditEntry.RequiresReason(CreditEntryKind.Spend), Is.False);
            Assert.That(CreditEntry.RequiresReason(CreditEntryKind.Expiry), Is.False);
        });
    }

    /// <summary>The registration the startup file calls, by the exact name and shape it expects.
    /// </summary>
    [Test]
    public void The_registration_extension_exists_under_its_agreed_name()
    {
        var method = typeof(CreditServiceCollectionExtensions).GetMethod("AddCreditLedger");

        Assert.Multiple(() =>
        {
            Assert.That(method, Is.Not.Null);
            Assert.That(method!.GetParameters(), Has.Length.EqualTo(1));
            Assert.That(typeof(CreditServiceCollectionExtensions).Namespace,
                Is.EqualTo("Billing.Application.Credit"));
        });
    }
}
