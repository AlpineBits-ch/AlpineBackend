using AppEnvironment;

namespace Billing.Tests;

/// <summary>
/// What an operator is told about their Stripe configuration, and what counts as configured.
/// </summary>
[TestFixture]
public class StripeCredentialDefaultsTests
{
    private const string SomeSandboxSecret = "sk_test_notarealkey";
    private const string SomeSandboxPublishable = "pk_test_notarealkey";

    private string _secret = null!;
    private string _publishable = null!;
    private string _webhook = null!;

    [SetUp]
    public void SetUp()
    {
        _secret = Env.License.StripeSecretKey;
        _publishable = Env.License.StripePublishableKey;
        _webhook = Env.License.StripeWebhookSecret;
    }

    [TearDown]
    public void TearDown()
    {
        Env.License.StripeSecretKey = _secret;
        Env.License.StripePublishableKey = _publishable;
        Env.License.StripeWebhookSecret = _webhook;
    }

    /// <summary>An operator part-way through setup: sandbox keys pasted in, webhook not yet done.</summary>
    private static void UseSandboxKeysWithoutAWebhookSecret()
    {
        Env.License.StripeSecretKey = SomeSandboxSecret;
        Env.License.StripePublishableKey = SomeSandboxPublishable;
        Env.License.StripeWebhookSecret = string.Empty;
    }

    private static void UseLiveCredentials()
    {
        Env.License.StripeSecretKey = "sk_live_notarealkey";
        Env.License.StripePublishableKey = "pk_live_notarealkey";
        Env.License.StripeWebhookSecret = "whsec_anoperatorsownsecret";
    }

    [Test]
    public void A_half_configured_instance_names_all_three_credentials()
    {
        UseSandboxKeysWithoutAWebhookSecret();

        // Two for being test-mode, one for being absent.
        Assert.That(Env.License.TestModeStripeCredentials, Is.EquivalentTo(new[]
        {
            "STRIPE_SECRET_KEY", "STRIPE_PUBLISHABLE_KEY", "STRIPE_WEBHOOK_SECRET",
        }));
    }

    [Test]
    public void Fully_overridden_live_credentials_name_nothing()
    {
        UseLiveCredentials();

        Assert.That(Env.License.TestModeStripeCredentials, Is.Empty);
    }

    /// <summary>
    /// The half-configured case this really exists for: somebody set the secret key and missed the
    /// other two. Nothing about the running service looks wrong.
    /// </summary>
    [Test]
    public void A_live_secret_key_beside_forgotten_ones_names_only_the_forgotten()
    {
        UseSandboxKeysWithoutAWebhookSecret();
        Env.License.StripeSecretKey = "sk_live_notarealkey";

        Assert.That(Env.License.TestModeStripeCredentials, Is.EquivalentTo(new[]
        {
            "STRIPE_PUBLISHABLE_KEY", "STRIPE_WEBHOOK_SECRET",
        }));
    }

    /// <summary>
    /// A signing secret has no <c>_test_</c> marker - a live one and a sandbox one are both
    /// <c>whsec_</c> and both unguessable - so the only bad state to detect is its absence.
    /// </summary>
    [Test]
    public void An_operators_own_webhook_secret_is_not_flagged_even_against_a_sandbox()
    {
        UseSandboxKeysWithoutAWebhookSecret();
        Env.License.StripeWebhookSecret = "whsec_theirownunguessableone";

        Assert.Multiple(() =>
        {
            Assert.That(Env.License.TestModeStripeCredentials,
                Does.Not.Contain("STRIPE_WEBHOOK_SECRET"));
            Assert.That(Env.License.IsStripeWebhookConfigured, Is.True);
        });
    }

    /// <summary>
    /// The negative case, and the reason this credential is treated differently from the two keys.
    /// </summary>
    [Test]
    public void An_absent_webhook_secret_is_named_and_reads_as_unconfigured()
    {
        UseSandboxKeysWithoutAWebhookSecret();

        Assert.Multiple(() =>
        {
            Assert.That(Env.License.IsStripeWebhookConfigured, Is.False);
            Assert.That(Env.License.TestModeStripeCredentials,
                Does.Contain("STRIPE_WEBHOOK_SECRET"));
        });
    }

    [Test]
    public void Whitespace_is_not_a_webhook_secret()
    {
        UseLiveCredentials();
        Env.License.StripeWebhookSecret = "   ";

        Assert.That(Env.License.IsStripeWebhookConfigured, Is.False);
    }

    [Test]
    public void A_test_key_from_any_account_is_still_test_mode()
    {
        UseLiveCredentials();
        Env.License.StripeSecretKey = "sk_test_someoneelsessandbox";

        // Detected from the value, not from a match against a known constant, so a second sandbox is
        // caught too. A flag somebody had to remember to set would not be.
        Assert.That(Env.License.TestModeStripeCredentials, Does.Contain("STRIPE_SECRET_KEY"));
    }

    /// <summary>The gate, asserted so it cannot change quietly.</summary>
    [Test]
    public void An_instance_with_no_secret_key_is_not_stripe_configured()
    {
        UseLiveCredentials();
        Env.License.StripeSecretKey = string.Empty;

        Assert.That(Env.License.IsStripeConfigured, Is.False);
    }

    [Test]
    public void A_sandbox_key_still_counts_as_configured()
    {
        UseSandboxKeysWithoutAWebhookSecret();

        // Test-mode is a warning, not a disqualification: a sandbox key genuinely does let the service
        // talk to Stripe, which is exactly what this gate asks.
        Assert.That(Env.License.IsStripeConfigured, Is.True);
    }
}
