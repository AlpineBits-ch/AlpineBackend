using AppEnvironment;

namespace Billing.Application.Stripe;

/// <summary>Everything this service needs to talk to Stripe.</summary>
public sealed class StripeOptions
{
    public const string SectionName = "Billing:Stripe";

    /// <summary>How long after a failed payment the subject keeps what they were on.</summary>
    public int DunningGraceDays { get; set; } = 7;

    public TimeSpan DunningGrace => TimeSpan.FromDays(DunningGraceDays);

    /// <summary><c>sk_...</c>.</summary>
    public string SecretKey => Env.License.StripeSecretKey;

    /// <summary><c>whsec_...</c>.</summary>
    public string WebhookSecret => Env.License.StripeWebhookSecret;

    /// <summary>Whether there is a key to call Stripe with at all.</summary>
    public bool IsConfigured => Env.License.IsStripeConfigured;
}
