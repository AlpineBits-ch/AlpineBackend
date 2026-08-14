using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Entitlements.Caching;

public static class EntitlementCacheServiceCollectionExtensions
{
    /// <summary>
    /// Puts the Redis cache in front of the resolver this service already registered.
    /// </summary>
    public static IServiceCollection AddEntitlementCache(
        this IServiceCollection services, Action<EntitlementCacheOptions>? configure = null)
    {
        var options = new EntitlementCacheOptions();
        configure?.Invoke(options);
        return services.AddEntitlementCache(options);
    }

    /// <summary>The same call for a caller that already holds the options object, which the gateway
    /// does: it derives the client-facing <c>ttlSeconds</c> from the same instance, so the two
    /// numbers cannot drift apart into a client cache that outlives the server's backstop.</summary>
    public static IServiceCollection AddEntitlementCache(
        this IServiceCollection services, EntitlementCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new EntitlementSetCodec());

        services.TryAddSingleton<IEntitlementCacheStore>(provider =>
            provider.GetService<IDistributedCache>() is { } cache
                ? new DistributedEntitlementCacheStore(cache, Logger<DistributedEntitlementCacheStore>(provider))
                : new DisabledEntitlementCacheStore());

        // Resolved from the container rather than computed here, because the sources are registered
        // by the caller and the last of them may be registered after this call.
        services.TryAddSingleton(provider => new EntitlementCacheKeyspace(
            options.KeyPrefix,
            EntitlementCacheKeyspace.FingerprintOf(
                provider.GetServices<IEntitlementSource>(),
                provider.GetService<EntitlementPlanOptions>())));

        services.TryAddSingleton(provider => new EntitlementCacheInvalidator(
            provider.GetRequiredService<IEntitlementCacheStore>(),
            provider.GetRequiredService<EntitlementCacheKeyspace>(),
            provider.GetRequiredService<EntitlementSetCodec>(),
            provider.GetRequiredService<EntitlementCacheOptions>(),
            Logger<EntitlementCacheInvalidator>(provider),
            provider.GetService<TimeProvider>()));

        services.Replace(ServiceDescriptor.Singleton(provider => (EntitlementResolver)new CachedEntitlementResolver(
            provider.GetServices<IEntitlementSource>().ToList(),
            provider.GetRequiredService<IEntitlementCacheStore>(),
            provider.GetRequiredService<EntitlementCacheKeyspace>(),
            provider.GetRequiredService<EntitlementSetCodec>(),
            provider.GetRequiredService<EntitlementCacheOptions>(),
            Logger<CachedEntitlementResolver>(provider),
            provider.GetService<TimeProvider>())));

        return services;
    }

    /// <summary>Caches the entitlement version alongside the set.</summary>
    public static IServiceCollection AddEntitlementVersionCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var inner = services.LastOrDefault(service => service.ServiceType == typeof(IEntitlementVersionProvider))
                    ?? throw new InvalidOperationException(
                        "AddEntitlementVersionCache decorates an IEntitlementVersionProvider, and none is "
                        + "registered. Register the provider that reads Billing's counter first.");

        services.Remove(inner);

        services.AddSingleton<IEntitlementVersionProvider>(provider => new CachedEntitlementVersionProvider(
            (IEntitlementVersionProvider)Instantiate(provider, inner),
            provider.GetRequiredService<IEntitlementCacheStore>(),
            provider.GetRequiredService<EntitlementCacheKeyspace>(),
            provider.GetRequiredService<EntitlementCacheOptions>(),
            Logger<CachedEntitlementVersionProvider>(provider),
            provider.GetService<TimeProvider>()));

        return services;
    }

    /// <summary>The logger for a type, or a silent one.</summary>
    private static ILogger<T> Logger<T>(IServiceProvider provider) =>
        provider.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;

    /// <summary>Builds the decorated registration whichever of the three forms it took.</summary>
    private static object Instantiate(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance) return instance;
        if (descriptor.ImplementationFactory is { } factory) return factory(provider);

        return ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }
}
