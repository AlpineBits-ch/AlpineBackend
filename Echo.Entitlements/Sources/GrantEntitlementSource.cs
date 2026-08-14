using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Entitlements.Sources;

/// <summary>One grant, in the only shape this library can consume.</summary>
/// <param name="GrantId">
/// Shown verbatim as provenance, so it has to name something a human can then go and look up.
/// </param>
/// <param name="ExpiresAt">Null means permanent (spec section 6).</param>
/// <param name="StartsAt">
/// Null means immediately, which is what every grant meant before credit purchases needed to queue
/// (spec section 8.3).
/// </param>
public sealed record EntitlementGrant(
    string GrantId,
    string? Plan,
    IReadOnlyDictionary<string, string>? Entitlements,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? StartsAt = null)
{
    /// <summary>Whether this grant still counts at a given instant.</summary>
    public bool IsActiveAt(DateTimeOffset instant) =>
        (ExpiresAt is null || ExpiresAt > instant) && (StartsAt is null || StartsAt <= instant);
}

/// <summary>Where <see cref="GrantEntitlementSource"/> gets its grants.</summary>
public interface IGrantProvider
{
    Task<IReadOnlyList<EntitlementGrant>> ActiveGrantsAsync(
        EntitlementSubject subject, CancellationToken cancellationToken);
}

/// <summary>Admin and campaign grants, as a source (spec section 6).</summary>
public sealed class GrantEntitlementSource : IEntitlementSource
{
    private readonly IGrantProvider _grants;
    private readonly PlanCatalogue _plans;
    private readonly TimeProvider _clock;

    /// <param name="band">Which of the two grant bands this instance speaks for.</param>
    public GrantEntitlementSource(
        IGrantProvider grants,
        PlanCatalogue plans,
        TimeProvider? clock = null,
        EntitlementPrecedence band = EntitlementPrecedence.AdminGrant)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(plans);

        if (band is not (EntitlementPrecedence.AdminGrant or EntitlementPrecedence.PromotionalGrant))
        {
            throw new ArgumentException(
                $"A grant source sits at {EntitlementPrecedence.AdminGrant} or "
                + $"{EntitlementPrecedence.PromotionalGrant}; {band} is a different kind of source.",
                nameof(band));
        }

        _grants = grants;
        _plans = plans;
        _clock = clock ?? TimeProvider.System;
        Precedence = band;
    }

    public EntitlementPrecedence Precedence { get; }

    public async Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var grants = await _grants.ActiveGrantsAsync(subject, cancellationToken).ConfigureAwait(false);
        if (grants.Count == 0) return EntitlementSet.Empty;

        var now = _clock.GetUtcNow();

        // Merged here rather than by handing every grant to the builder in turn, because the
        // builder overwrites provenance on every Set while keeping the more generous value.
        var winners = new Dictionary<EntitlementKey, (EntitlementValue Value, string GrantId)>();

        foreach (var grant in grants)
        {
            if (!grant.IsActiveAt(now)) continue;
            Contribute(grant, winners);
        }

        if (winners.Count == 0) return EntitlementSet.Empty;

        var builder = new EntitlementSetBuilder(Precedence);
        foreach (var (key, winner) in winners) builder.Set(key, winner.Value, winner.GrantId);

        return builder.Build();
    }

    private void Contribute(
        EntitlementGrant grant, Dictionary<EntitlementKey, (EntitlementValue Value, string GrantId)> winners)
    {
        if (!string.IsNullOrWhiteSpace(grant.Plan) && _plans.Find(grant.Plan) is { } plan)
        {
            foreach (var (key, value) in plan.Values) Offer(winners, key, value, grant.GrantId);
        }

        if (grant.Entitlements is null) return;

        foreach (var (name, text) in grant.Entitlements)
        {
            if (text is null || !EntitlementKeys.TryGet(name, out var key)) continue;
            if (!TryParse(key, text, out var value)) continue;

            Offer(winners, key, value, grant.GrantId);
        }
    }

    /// <summary>The same rule the resolver applies between sources: the value is the more generous
    /// of the two, and the credit moves only when the incoming grant strictly beat what was there,
    /// so a tie stays with the grant that got there first.</summary>
    private static void Offer(
        Dictionary<EntitlementKey, (EntitlementValue Value, string GrantId)> winners,
        EntitlementKey key,
        EntitlementValue value,
        string grantId)
    {
        if (!winners.TryGetValue(key, out var existing))
        {
            winners[key] = (value, grantId);
            return;
        }

        var merged = EntitlementValue.Merge(existing.Value, value);
        winners[key] = merged.Raw > existing.Value.Raw ? (merged, grantId) : (merged, existing.GrantId);
    }

    private static bool TryParse(EntitlementKey key, string text, out EntitlementValue value)
    {
        try
        {
            value = key.Parse(text);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // FormatException from a flag or a numeric limit, ArgumentException from a rung that is
            // not on the ladder.
            value = default;
            return false;
        }
    }
}

public static class GrantEntitlementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the grant source over whatever <see cref="IGrantProvider"/> the caller has
    /// registered.
    /// </summary>
    public static IServiceCollection AddGrantEntitlementSource(
        this IServiceCollection services,
        EntitlementPrecedence band = EntitlementPrecedence.AdminGrant)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IEntitlementSource>(provider => new GrantEntitlementSource(
            provider.GetRequiredService<IGrantProvider>(),
            provider.GetRequiredService<PlanCatalogue>(),
            provider.GetService<TimeProvider>(),
            band));

        return services;
    }
}
