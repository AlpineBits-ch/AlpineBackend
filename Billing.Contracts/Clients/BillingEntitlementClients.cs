using Billing.Contracts.Bus.Request;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Billing.Contracts.Clients;

/// <summary>How much of a broker round trip a caller will wait for before giving up.</summary>
public sealed class BillingClientOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a fetched plan catalogue is used before it is fetched again.</summary>
    public TimeSpan CatalogueTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the last known catalogue is served before Billing is tried again.
    /// </summary>
    public TimeSpan CatalogueOutageGrace { get; set; } = TimeSpan.FromSeconds(10);
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

/// <summary>
/// <see cref="IPlanCatalogueSource"/> over Billing's plan table, for the services that enforce
/// plans but do not own them.
/// </summary>
public class BusPlanCatalogueSource : IPlanCatalogueSource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BillingClientOptions _options;
    private readonly PlanCatalogue _configured;
    private readonly ILogger<BusPlanCatalogueSource>? _logger;
    private readonly TimeProvider _clock;

    /// <summary>One refresh at a time per process.</summary>
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private PlanCatalogue _current;
    private DateTimeOffset _freshUntil = DateTimeOffset.MinValue;
    private bool _fetched;

    public BusPlanCatalogueSource(
        IServiceScopeFactory scopes,
        BillingClientOptions options,
        PlanCatalogue configured,
        ILogger<BusPlanCatalogueSource>? logger = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configured);

        _scopes = scopes;
        _options = options;
        _configured = configured;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        _current = configured;
        Revision = PlanCatalogueRevision.Of(configured);
    }

    public string Revision { get; private set; }

    /// <summary>The default plan Billing publishes for a kind of subject, or null before the first
    /// successful fetch. The caller falls back to its own configuration on null.</summary>
    public string? DefaultGuildPlan { get; private set; }

    public string? DefaultUserPlan { get; private set; }

    public async ValueTask<PlanCatalogue> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_clock.GetUtcNow() < _freshUntil) return _current;

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = _clock.GetUtcNow();
            if (now < _freshUntil) return _current;

            try
            {
                var response = await FetchAsync(cancellationToken).ConfigureAwait(false)
                               ?? throw new InvalidOperationException(
                                   "Billing did not answer the plan catalogue request.");

                Apply(response);
                _freshUntil = now + _options.CatalogueTtl;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _freshUntil = now + _options.CatalogueOutageGrace;

                _logger?.LogWarning(exception,
                    "Could not read the plan catalogue from Billing. Serving {Source} and retrying in "
                    + "{Grace}. An empty catalogue would put every subject on this instance at its "
                    + "catalogue defaults at once, which is why this falls back rather than clearing.",
                    _fetched ? "the last catalogue this process read" : "the configured catalogue",
                    _options.CatalogueOutageGrace);
            }

            return _current;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Forces the next read to go back to Billing.</summary>
    public void Invalidate() => _freshUntil = DateTimeOffset.MinValue;

    /// <summary>Which plan an unassigned subject of this kind is on, according to Billing. Loads the
    /// catalogue if it has not been read yet, because the defaults arrive with it.</summary>
    public async ValueTask<string?> DefaultForAsync(
        SubjectKind kind, CancellationToken cancellationToken = default)
    {
        await CurrentAsync(cancellationToken).ConfigureAwait(false);
        return kind == SubjectKind.Guild ? DefaultGuildPlan : DefaultUserPlan;
    }

    /// <summary>The broker call, and the seam a test replaces.</summary>
    protected virtual async Task<GetPlanCatalogueResponse?> FetchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        return await bus.InvokeAsync<GetPlanCatalogueResponse>(
            new GetPlanCatalogueRequest(), cancellationToken, _options.Timeout);
    }

    private void Apply(GetPlanCatalogueResponse response)
    {
        var definitions = new List<PlanDefinition>(response.Plans.Count);

        foreach (var plan in response.Plans)
        {
            if (string.IsNullOrWhiteSpace(plan.Name)) continue;
            definitions.Add(Definition(plan));
        }

        // Configuration is still underneath, for a plan this build's configuration names and the
        // published catalogue does not.
        var published = definitions.Select(plan => plan.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        definitions.AddRange(_configured.Plans.Where(plan => !published.Contains(plan.Name)));

        _current = new PlanCatalogue(definitions);
        Revision = PlanCatalogueRevision.Of(_current);
        DefaultGuildPlan = Blank(response.DefaultGuildPlan);
        DefaultUserPlan = Blank(response.DefaultUserPlan);
        _fetched = true;
    }

    /// <summary>
    /// A key this build does not know, or a value it cannot parse, is skipped rather than thrown on
    /// - the same posture <c>GrantEntitlementSource</c> and Billing's own read path take.
    /// </summary>
    private PlanDefinition Definition(CataloguePlanDto plan)
    {
        var parsed = new Dictionary<EntitlementKey, EntitlementValue>();

        foreach (var (name, text) in plan.Values)
        {
            if (text is null || !EntitlementKeys.TryGet(name, out var key))
            {
                _logger?.LogWarning(
                    "Billing's plan '{Plan}' names entitlement key '{Key}', which this build does not "
                    + "have. Skipping it; the rest of the plan still resolves.", plan.Name, name);
                continue;
            }

            try
            {
                parsed[key] = key.Parse(text);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                _logger?.LogWarning(exception,
                    "Billing's plan '{Plan}' sets '{Key}' to a value it cannot take here. Skipping the key.",
                    plan.Name, name);
            }
        }

        return new PlanDefinition(plan.Name, parsed, plan.DisplayName);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary><see cref="IPlanAssignment"/> over Billing's <c>PlanAssignment</c> rows.</summary>
public class BusPlanAssignment : IPlanAssignment
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BillingClientOptions _options;
    private readonly BusPlanCatalogueSource _catalogue;
    private readonly FixedPlanAssignment _configured;

    public BusPlanAssignment(
        IServiceScopeFactory scopes,
        BillingClientOptions options,
        BusPlanCatalogueSource catalogue,
        FixedPlanAssignment configured)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(configured);

        _scopes = scopes;
        _options = options;
        _catalogue = catalogue;
        _configured = configured;
    }

    public async ValueTask<string?> PlanNameForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var response = await AssignmentAsync(subject, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            throw new InvalidOperationException(
                $"Billing did not answer the plan assignment request for {subject}. Treated as an outage "
                + "rather than as 'no plan', so the last known entitlements are served instead.");
        }

        if (!string.IsNullOrWhiteSpace(response.PlanReference)) return response.PlanReference;

        // Billing's default first, then this service's own configuration.
        return await _catalogue.DefaultForAsync(subject.Kind, cancellationToken).ConfigureAwait(false)
               ?? _configured.DefaultFor(subject.Kind);
    }

    /// <summary>The broker call, and the seam a test replaces.</summary>
    protected virtual async Task<GetPlanAssignmentResponse?> AssignmentAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        return await bus.InvokeAsync<GetPlanAssignmentResponse>(
            new GetPlanAssignmentRequest { SubjectKind = subject.Kind, SubjectId = subject.Id },
            cancellationToken,
            _options.Timeout);
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

    /// <summary>
    /// Registers the plan catalogue and the plan assignment over the bus, replacing the two
    /// configuration-backed ones <c>AddEntitlements</c> registered.
    /// </summary>
    public static IServiceCollection AddBillingPlanSource(
        this IServiceCollection services, Action<BillingClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(Options(configure));

        services.TryAddSingleton(provider => new BusPlanCatalogueSource(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<BillingClientOptions>(),
            provider.GetRequiredService<PlanCatalogue>(),
            provider.GetService<ILogger<BusPlanCatalogueSource>>(),
            provider.GetService<TimeProvider>()));

        services.Replace(ServiceDescriptor.Singleton<IPlanCatalogueSource>(
            provider => provider.GetRequiredService<BusPlanCatalogueSource>()));

        services.Replace(ServiceDescriptor.Singleton<IPlanAssignment>(provider => new BusPlanAssignment(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<BillingClientOptions>(),
            provider.GetRequiredService<BusPlanCatalogueSource>(),
            provider.GetRequiredService<FixedPlanAssignment>())));

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
