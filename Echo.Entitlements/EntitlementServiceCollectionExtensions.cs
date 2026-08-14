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

        // The configured catalogue and the configured default, registered as their concrete types
        // as well as behind their interfaces.
        services.TryAddSingleton(provider => new FixedPlanCatalogueSource(
            provider.GetRequiredService<PlanCatalogue>()));
        services.TryAddSingleton<IPlanCatalogueSource>(provider =>
            provider.GetRequiredService<FixedPlanCatalogueSource>());

        services.TryAddSingleton(new FixedPlanAssignment(options));
        services.TryAddSingleton<IPlanAssignment>(provider =>
            provider.GetRequiredService<FixedPlanAssignment>());

        // Constructed by hand because the source has two constructors - one over a catalogue source,
        // one over a fixed catalogue - and both are resolvable, which the container treats as an
        // ambiguity rather than as a preference.
        services.AddSingleton<IEntitlementSource>(provider => new PlanDefaultEntitlementSource(
            provider.GetRequiredService<IPlanCatalogueSource>(),
            provider.GetRequiredService<IPlanAssignment>()));

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
