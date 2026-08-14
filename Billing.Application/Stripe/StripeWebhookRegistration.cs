namespace Billing.Application.Stripe;

/// <summary>The webhook half of the Stripe integration, wired into the container.</summary>
public static class StripeWebhookRegistration
{
    public static IServiceCollection AddStripeWebhooks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, both of them: they write through the request's DbContext and rely on the Wolverine
        // middleware around that context to commit, so a singleton would be writing through a context
        // nothing owns.
        services.AddScoped<SubscriptionReconciler>();
        services.AddScoped<StripeWebhookProcessor>();

        // The elapsed-grace downgrades, which nothing arrives from Stripe to trigger.
        services.AddHostedService<DunningSweeper>();

        return services;
    }
}
