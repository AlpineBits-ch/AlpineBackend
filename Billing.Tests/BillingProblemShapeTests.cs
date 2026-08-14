using System.Text;
using System.Text.Json;
using Billing.Application.Dtos;
using Billing.Application.Endpoints;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Billing.Tests;

/// <summary>Where the machine-readable <c>code</c> actually lands in a problem body.</summary>
[TestFixture]
public class BillingProblemShapeTests
{
    private static async Task<(int Status, string ContentType, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        var json = Encoding.UTF8.GetString(body.ToArray());

        return (context.Response.StatusCode,
            context.Response.ContentType ?? string.Empty,
            JsonDocument.Parse(json).RootElement);
    }

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task A_refusal_is_problem_json_with_the_code_as_a_top_level_member()
    {
        var (status, contentType, body) = await ExecuteAsync(
            BillingProblems.From(new CheckoutRefusedException(
                BillingErrorCodes.AlreadySubscribed, StatusCodes.Status409Conflict,
                "This already has a live subscription.")));

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(409));
            Assert.That(contentType, Does.StartWith("application/problem+json"));
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("already_subscribed"));
            Assert.That(body.GetProperty("status").GetInt32(), Is.EqualTo(409));
            Assert.That(body.GetProperty("detail").GetString(),
                Is.EqualTo("This already has a live subscription."));

            // The other position the client also reads.
            Assert.That(body.TryGetProperty("extensions", out _), Is.False);
        });
    }

    [Test]
    public async Task A_Stripe_failure_is_a_502_carrying_Stripes_own_message()
    {
        var (status, _, body) = await ExecuteAsync(
            BillingProblems.From(new StripeGatewayException(
                "subscriptions.create", "Your card was declined.", null, "card_declined")));

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(502));
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("stripe_error"));
            Assert.That(body.GetProperty("detail").GetString(), Does.Contain("Your card was declined."));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>The guard is what puts every endpoint's two failure modes into one shape.</summary>
    [Test]
    public async Task The_guard_turns_a_thrown_refusal_into_the_same_problem_body()
    {
        var result = await BillingProblems.GuardAsync(
            () => throw CheckoutRefusedException.NotThePayer());

        var (status, _, body) = await ExecuteAsync(result);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(403));
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("not_the_payer"));
        });
    }

    [Test]
    public async Task The_guard_returns_a_successful_result_untouched()
    {
        var result = await BillingProblems.GuardAsync(() => Task.FromResult(Results.NoContent()));

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        await result.ExecuteAsync(context);

        Assert.That(context.Response.StatusCode, Is.EqualTo(204));
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>Anything that is not a billing refusal or a Stripe failure is a bug, and a bug must
    /// keep its stack trace and its 500 rather than being flattened into a customer-facing
    /// sentence.</summary>
    [Test]
    public void The_guard_does_not_swallow_a_programming_error()
    {
        Assert.That(
            async () => await BillingProblems.GuardAsync(
                () => throw new InvalidOperationException("a real bug")),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Every_code_the_contract_names_has_a_constant()
    {
        var codes = new[]
        {
            BillingErrorCodes.BillingDisabled,
            BillingErrorCodes.NotPurchasable,
            BillingErrorCodes.AlreadySubscribed,
            BillingErrorCodes.NotPermitted,
            BillingErrorCodes.NotThePayer,
            BillingErrorCodes.SubscriptionLapsed,
            BillingErrorCodes.LastPaymentMethod,
            BillingErrorCodes.StripeError,
        };

        Assert.That(codes, Is.EqualTo(new[]
        {
            "billing_disabled", "not_purchasable", "already_subscribed", "not_permitted",
            "not_the_payer", "subscription_lapsed", "last_payment_method", "stripe_error",
        }));
    }
}
