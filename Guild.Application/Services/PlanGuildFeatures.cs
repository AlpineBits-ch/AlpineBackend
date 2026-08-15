using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Guild.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Guild.Application.Services;

/// <summary>
/// Which modules each plan covers, by plan name.
///
/// <para><b>Empty is the shipped state and it means "every plan covers everything".</b> The pricing
/// model tiers voice capacity, storage, emoji and bots, and deliberately tiers no module at all - it
/// says in as many words that what a plan buys is voice capacity and never the module's existence.
/// So there is nothing here to configure until somebody decides otherwise, and that decision is a
/// commercial one that belongs in an operator's configuration rather than in this service's
/// source.</para>
///
/// <para>A value is either <see cref="Everything"/> or a comma-separated list of
/// <see cref="GuildFeatures"/> names. Names rather than a number, for the reason
/// <c>GuildFeatureMap.Names</c> gives: an unconstrained mask is <c>~None</c>, which as an integer is
/// larger than anything a JSON client can hold, and a configuration file that carried one would be
/// unreadable to the person editing it.</para>
/// </summary>
/// <example>
/// <code>
/// "Entitlements": {
///   "GuildFeatures": {
///     "free": "Wiki,Events,Threads",
///     "plus": "*",
///     "pro":  "*"
///   }
/// }
/// </code>
/// </example>
public sealed class GuildPlanFeatureBundles
{
    public const string SectionName = "Entitlements:GuildFeatures";

    /// <summary>The value that means "this plan withholds nothing".</summary>
    public const string Everything = "*";

    private readonly Dictionary<string, GuildFeatures> _byPlan;

    public GuildPlanFeatureBundles(
        IReadOnlyDictionary<string, string>? configured, ILogger? logger = null)
    {
        _byPlan = new Dictionary<string, GuildFeatures>(StringComparer.OrdinalIgnoreCase);

        foreach (var (plan, value) in configured ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(plan)) continue;
            _byPlan[plan.Trim()] = Parse(plan, value, logger);
        }
    }

    /// <summary>True when no plan has a bundle configured, which is every instance today.</summary>
    public bool IsEmpty => _byPlan.Count == 0;

    /// <summary>
    /// The modules a plan covers, or null when this plan has no bundle configured.
    /// </summary>
    public GuildFeatures? For(string planReference)
    {
        if (_byPlan.TryGetValue(planReference, out var exact)) return exact;

        var (name, _) = PlanReference.Split(planReference);

        return _byPlan.TryGetValue(name, out var bundle) ? bundle : null;
    }

    /// <summary>An unreadable name is skipped rather than thrown on, the same posture every other
    /// reader of plan configuration takes: a bundle short one module still gates the rest, whereas a
    /// startup failure over one typo takes the whole service down for a value that fails open
    /// anyway. Both halves are logged, because a silently dropped module is a support ticket nobody
    /// can trace.</summary>
    private static GuildFeatures Parse(string plan, string? value, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == Everything) return ~GuildFeatures.None;

        var features = GuildFeatures.None;

        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Enum.TryParse takes a bare number as well as a name, and a number here would be the
            // raw mask this configuration format exists to keep out of an operator's hands.
            if (!char.IsLetter(name[0]) || !Enum.TryParse<GuildFeatures>(name, ignoreCase: true, out var feature))
            {
                logger?.LogWarning(
                    "Plan '{Plan}' names module '{Module}', which is not a GuildFeatures name. Skipping "
                    + "it; the rest of the bundle still applies.", plan, name);
                continue;
            }

            features |= feature;
        }

        return features;
    }
}

/// <summary><see cref="IGuildPlanFeatures"/> over the plan a guild is actually on.</summary>
public sealed class PlanGuildFeatures(
    IPlanAssignment assignment,
    GuildPlanFeatureBundles bundles,
    IMemoryCache cache,
    ILogger<PlanGuildFeatures>? logger = null) : IGuildPlanFeatures
{
    /// <summary>The same number as <c>EntitlementCacheOptions.Ttl</c>, and for the same reason: it
    /// decides how wrong the world is allowed to get when an invalidation never arrives, not how
    /// fast a plan change takes effect.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private const GuildFeatures Everything = ~GuildFeatures.None;

    public async Task<GuildFeatures> IncludedFeaturesAsync(
        string guildId, CancellationToken cancellationToken = default)
    {
        // Nothing configured is not a lookup worth doing.
        if (bundles.IsEmpty) return Everything;

        if (cache.TryGetValue<GuildFeatures>(Key(guildId), out var cached)) return cached;

        var included = await ResolveAsync(guildId, cancellationToken);

        cache.Set(Key(guildId), included, Ttl);
        return included;
    }

    /// <summary>Drops what is memoised for one guild, for a caller that knows its plan moved and does
    /// not want to wait out <see cref="Ttl"/>.</summary>
    public void Invalidate(string guildId) => cache.Remove(Key(guildId));

    private static string Key(string guildId) => $"guild-plan-features:{guildId}";

    private async Task<GuildFeatures> ResolveAsync(string guildId, CancellationToken cancellationToken)
    {
        string? plan;

        try
        {
            plan = await assignment.PlanNameForAsync(
                EntitlementSubject.ForGuild(guildId), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception,
                "Could not resolve the plan for guild {Guild}, so no module is withheld from it. An "
                + "outage that paywalled modules would be a worse incident than a guild briefly "
                + "holding one it stopped paying for.", guildId);
            return Everything;
        }

        if (plan is null)
        {
            // A real state rather than a gap: an instance that configured no default plan for guilds
            // resolves every key to its catalogue default, and a guild on no plan is not a guild on
            // the most restrictive one.
            logger?.LogDebug(
                "Guild {Guild} is on no plan, so no module is withheld from it.", guildId);
            return Everything;
        }

        if (bundles.For(plan) is not { } included)
        {
            logger?.LogWarning(
                "Plan '{Plan}' has no module bundle configured, so guild {Guild} keeps every module. "
                + "Add it under '{Section}' if that plan is meant to withhold any.",
                plan, guildId, GuildPlanFeatureBundles.SectionName);
            return Everything;
        }

        // The never-paywall floor, stated on the way out as well as applied on the way in.
        return included | GuildFeatureMap.PlanIndependentFeatures;
    }
}

public static class GuildPlanFeatureExtensions
{
    /// <summary>Registers the plan-to-module clamp.</summary>
    public static IServiceCollection AddGuildPlanFeatures(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddMemoryCache();

        services.TryAddSingleton(provider => new GuildPlanFeatureBundles(
            configuration.GetSection(GuildPlanFeatureBundles.SectionName)
                .Get<Dictionary<string, string>>(),
            provider.GetService<ILogger<GuildPlanFeatureBundles>>()));

        services.TryAddSingleton<PlanGuildFeatures>();
        services.TryAddSingleton<IGuildPlanFeatures>(
            provider => provider.GetRequiredService<PlanGuildFeatures>());

        return services;
    }
}
