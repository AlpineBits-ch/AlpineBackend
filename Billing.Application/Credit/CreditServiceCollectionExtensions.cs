using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Billing.Application.Credit;

/// <summary>Everything the credit wallet needs, in one call.</summary>
public static class CreditServiceCollectionExtensions
{
    public static IServiceCollection AddCreditLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Bound through the container's IConfiguration rather than from a parameter, so this stays
        // a one-argument call the startup file can add without threading configuration through it.
        services.AddOptions<CreditOptions>().BindConfiguration(CreditOptions.SectionName);

        services.AddScoped<CreditLedgerService>();
        services.AddScoped<CreditCatalogueService>();
        services.AddScoped<CreditCampaignService>();
        services.AddScoped<CreditPurchaseService>();

        services.AddHostedService<CreditExpirySweeper>();

        // Registered unconditionally and gated at the top of its own pass instead, on both
        // CreditOptions.RenewFromCredit and whether Stripe is configured at all.
        services.AddHostedService<CreditRenewalSweeper>();

        return services;
    }
}
