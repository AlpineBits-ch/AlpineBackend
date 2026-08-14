using System.Reflection;
using Billing.Application.Endpoints;
using Billing.Application.Stripe;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Billing.Tests;

/// <summary>The shape of the webhook endpoint, asserted against the method itself.</summary>
[TestFixture]
public class StripeWebhookEndpointTests
{
    private static MethodInfo Receive =>
        typeof(StripeWebhookEndpoint).GetMethod(nameof(StripeWebhookEndpoint.ReceiveAsync))!;

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void Nothing_on_the_endpoint_binds_the_request_body()
    {
        var bound = Receive.GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Where(parameter => parameter.GetCustomAttribute<NotBodyAttribute>() is null)
            .Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}")
            .ToList();

        Assert.That(bound, Is.Empty,
            "a parameter Wolverine can bind means the body is read and re-serialised before the "
            + "signature is checked, and the signature covers the exact bytes Stripe sent");
    }

    /// <summary>Stripe holds no token of ours and never will, so there is nothing to put in front of
    /// this route. The signature is the authentication, which is also why an unset secret refuses
    /// rather than trusts.</summary>
    [Test]
    public void The_endpoint_is_anonymous()
    {
        Assert.That(Receive.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Not.Null);
    }

    [Test]
    public void The_endpoint_is_a_post_on_the_documented_route()
    {
        var post = Receive.GetCustomAttribute<WolverinePostAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(post, Is.Not.Null);
            Assert.That(post!.Template, Is.EqualTo("/api/v1/stripe/webhook"));
            Assert.That(StripeWebhookEndpoint.Route, Is.EqualTo("/api/v1/stripe/webhook"));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The gateway strips <c>/billing</c> and forwards the rest, so the public URL and the route
    /// the service maps differ by exactly that segment.
    /// </summary>
    [Test]
    public void The_public_path_is_the_route_behind_the_gateways_billing_segment()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StripeWebhookEndpoint.PublicPath,
                Is.EqualTo("/api/v1/billing/stripe/webhook"));

            Assert.That(StripeWebhookEndpoint.PublicPath.Replace("/billing", string.Empty),
                Is.EqualTo(StripeWebhookEndpoint.Route));
        });
    }

    /// <summary>The header the signature travels in, spelled once.</summary>
    [Test]
    public void The_signature_header_is_the_one_Stripe_sends()
    {
        Assert.That(StripeWebhookProcessor.SignatureHeader, Is.EqualTo("Stripe-Signature"));
    }
}
