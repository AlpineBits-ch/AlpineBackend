using AppEnvironment;
using Billing.Contracts.Clients;
using Echo.Entitlements;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Api;

/// <summary>The public-listing plan gate and everything that backs it, in one call.</summary>
public static class DiscoveryEntitlements
{
    // Shared by Program.cs and its test so both register the same set - it does not prove anything
    // still calls this method.
    public static IServiceCollection AddDiscoveryEntitlements(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddEntitlements(configuration);
        services.AddLicenseMode(
            LicenseModes.Parse(Env.License.Mode), OperatorCeilings.Parse(Env.License.OperatorCeilings));

        // Billing-backed sources, hosted only - self-host runs on SelfHostEverythingSource alone.
        if (Env.License.IsHosted && Env.License.IsBillingConfigured)
        {
            services.AddBillingGrantSource();
            services.AddBillingPlanSource();
        }

        services.AddEntitlementCache();

        return services;
    }
}
