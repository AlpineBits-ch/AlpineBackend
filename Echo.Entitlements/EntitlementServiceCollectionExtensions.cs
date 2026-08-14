using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Echo.Entitlements;

public static class EntitlementServiceCollectionExtensions
{
    /// <summary>Registers the entitlement resolver and the plan catalogue behind it.</summary>
    public static IServiceCollection AddEntitlements(
        this IServiceCollection services, Action<EntitlementPlanOptions>? configure = null)
    {
        var options = new EntitlementPlanOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(PlanCatalogue.FromOptions(options));

        // TryAdd so that the subscription source can replace it later without this call having to
        // know whether Billing is deployed.
        services.TryAddSingleton<IPlanAssignment>(_ => new FixedPlanAssignment(options));

        services.AddSingleton<IEntitlementSource, PlanDefaultEntitlementSource>();

        // Constructed by hand because the resolver's catalogue parameter is a test seam with a
        // default, and the container resolves constructor parameters rather than honouring defaults.
        services.TryAddSingleton(provider => new EntitlementResolver(provider.GetServices<IEntitlementSource>()));
        return services;
    }

    /// <summary>Binds the plan table from configuration.</summary>
    public static IServiceCollection AddEntitlements(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(EntitlementPlanOptions.SectionName);
        return services.AddEntitlements(options => section.Bind(options));
    }
}
