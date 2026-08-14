using Billing.Contracts.Bus.Request;
using Echo.Entitlements.Model;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Billing.Contracts.Clients;

/// <summary>How much of a broker round trip a caller will wait for before giving up.</summary>
public sealed class BillingClientOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary><see cref="IGrantProvider"/> for the services that are not Billing.</summary>
public sealed class BusGrantProvider(IServiceScopeFactory scopes, BillingClientOptions options) : IGrantProvider
{
    public async Task<IReadOnlyList<EntitlementGrant>> ActiveGrantsAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var response = await bus.InvokeAsync<GetActiveGrantsResponse>(
            new GetActiveGrantsRequest { SubjectKind = subject.Kind, SubjectId = subject.Id },
            cancellationToken,
            options.Timeout);

        if (response is null)
        {
            throw new InvalidOperationException(
                $"Billing did not answer the grant request for {subject}. Treated as an outage rather "
                + "than as 'no grants', so the last known entitlements are served instead.");
        }

        return response.Grants.Select(Map).ToList();
    }

    private static EntitlementGrant Map(ActiveGrantDto grant) =>
        new(grant.GrantId, grant.Plan, grant.Entitlements, grant.ExpiresAt);
}

/// <summary>
/// <see cref="IEntitlementVersionProvider"/> for the gateway, over Billing's counter.
/// </summary>
public sealed class BusEntitlementVersionProvider(IServiceScopeFactory scopes, BillingClientOptions options)
    : IEntitlementVersionProvider
{
    public async ValueTask<long> VersionAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var response = await bus.InvokeAsync<GetEntitlementVersionResponse>(
            new GetEntitlementVersionRequest { SubjectKind = subject.Kind, SubjectId = subject.Id },
            cancellationToken,
            options.Timeout);

        return response?.Version
               ?? throw new InvalidOperationException(
                   $"Billing did not answer the entitlement version request for {subject}.");
    }
}

public static class BillingEntitlementClientExtensions
{
    /// <summary>
    /// Registers the grant source over the bus, for a service that enforces entitlements but is not
    /// Billing.
    /// </summary>
    public static IServiceCollection AddBillingGrantSource(
        this IServiceCollection services, Action<BillingClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(Options(configure));

        // Singleton because GrantEntitlementSource is one, which is why the bus is reached through
        // a scope factory rather than injected.
        services.TryAddSingleton<IGrantProvider, BusGrantProvider>();
        services.AddGrantEntitlementSource();

        return services;
    }

    /// <summary>Registers the real entitlement version provider, replacing
    /// <c>StaticEntitlementVersionProvider</c>. The gateway is the only caller: it is the one
    /// component that builds a snapshot.</summary>
    public static IServiceCollection AddBillingEntitlementVersions(
        this IServiceCollection services, Action<BillingClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(Options(configure));
        services.Replace(ServiceDescriptor.Singleton<IEntitlementVersionProvider, BusEntitlementVersionProvider>());

        return services;
    }

    private static BillingClientOptions Options(Action<BillingClientOptions>? configure)
    {
        var options = new BillingClientOptions();
        configure?.Invoke(options);
        return options;
    }
}
